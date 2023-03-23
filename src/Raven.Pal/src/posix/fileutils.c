#ifndef _GNU_SOURCE
#define _GNU_SOURCE
#endif

#include <unistd.h>
#include <stdlib.h>
#include <stdio.h>
#include <sys/types.h>
#include <sys/stat.h>
#include <fcntl.h>
#include <errno.h>
#include <sys/mman.h>
#include <assert.h>
#include <time.h>
#include <string.h>

#include "rvn.h"
#include "status_codes.h"
#include "internal_posix.h"

PRIVATE int64_t
_pwrite(int32_t fd, void *buffer, uint64_t count, uint64_t offset, int32_t *detailed_error_code)
{
    uint64_t actually_written = 0;
    int64_t cifs_retries = 3;
    do
    {
        int64_t result = rvn_pwrite(fd, buffer + actually_written, count - actually_written, offset + actually_written);
        if (result < 0) /* we assume zero cannot be returned at any case as defined in POSIX */
        {
            if (errno == EINVAL && _sync_directory_allowed(fd) == SYNC_DIR_NOT_ALLOWED && --cifs_retries > 0)
            {
                /* cifs/nfs mount can sometimes fail on EINVAL after file creation
                lets give it few retries with short pauses between them - RavenDB-11954 */
                struct timespec ts;
                ts.tv_sec = 0;
                ts.tv_nsec = 200000000L; /* 200mSec */
                nanosleep(&ts, NULL);
                continue; /* retry cifs */
            }
            *detailed_error_code = errno;
            if (cifs_retries != 3)
                return FAIL_PWRITE_WITH_RETRIES;
            return FAIL_PWRITE;
        }
        actually_written += result;
    } while (actually_written < (int64_t)count);

    return SUCCESS;
}

PRIVATE int32_t
_allocate_file_space(int32_t fd, int64_t size, int32_t *detailed_error_code)
{
    int32_t result;
    int32_t retries;
    for (retries = 0; retries < 1024; retries++)
    {        
        result = _rvn_fallocate(fd, 0, size);

        switch (result)
        {
        case EBADF: /* aufs do not support fallocate (azure shares) */
        case EINVAL:
        case EFBIG: /* can occure on >4GB allocation on fs such as ntfs-3g, W95 FAT32, etc.*/
            /* fallocate is not supported, we'll use lseek instead */
            {
                char b = 0;
                int64_t rc = _pwrite(fd, &b, 1UL, (uint64_t)size - 1UL, detailed_error_code);
                if (rc != SUCCESS)
                    *detailed_error_code = errno;
                return rc;
            }
            break;
        case EINTR:
            *detailed_error_code = errno;
            continue; /* retry */

        case SUCCESS:
            return SUCCESS;

        default:
            *detailed_error_code = result;
            return FAIL_ALLOC_FILE;
        }
    }
    return result; /* return EINTR */
}

PRIVATE int32_t
_ensure_path_exists(const char *path, int32_t *detailed_error_code)
{
    assert(path != NULL);

    int32_t rc;
    /* An empty string should be dealt with as a 'current directory' like "." do (`stat()` fail in case of an empty string as parameter) */
    char *dup_path = path[0] == '\0' ? strdup(".") : strdup(path);
    if(dup_path == NULL)
    {
        rc = FAIL_NOMEM;
        goto error_cleanup;
    }
    char *current_end = dup_path;

    struct stat sb;
    while (current_end != NULL)
    {
        current_end = strchr(current_end + 1, '/');
        
        if (current_end != NULL)
            *current_end = '\0';

        if (stat(dup_path, &sb) == -1)
        {
            if (errno != ENOENT)
            {
                rc = FAIL_STAT_FILE;
                goto error_cleanup;
            }

            if (mkdir(dup_path, 0755) == -1)
            {
                *detailed_error_code = errno;
                char buf[1];
                if (readlink(dup_path, buf, 1) != -1 && errno != EINVAL)
                    rc = FAIL_BROKEN_LINK;
                else
                    rc = FAIL_CREATE_DIRECTORY;
                goto cleanup;
            }

            rc = _sync_directory_for_internal(dup_path, detailed_error_code);
            if (rc != SUCCESS)
                goto cleanup;

            rc = _sync_directory_for(dup_path, detailed_error_code);
            if (rc != SUCCESS)
                goto cleanup;
        }
        else if(S_ISDIR(sb.st_mode) == 0)
        {
            *detailed_error_code = ENOTDIR;
            rc = FAIL_NOT_DIRECTORY;
            goto cleanup;
        }

        if (current_end != NULL)
            *current_end = '/';
    }

    rc = SUCCESS;
    goto cleanup;

error_cleanup:
    *detailed_error_code = errno;
cleanup:
    if (dup_path != NULL)
        free(dup_path);
    return rc;
}

PRIVATE int32_t 
_open_file_to_read(const char *file_name, int32_t *fd, int32_t *detailed_error_code)
{
    *fd = open(file_name, O_RDONLY, S_IRUSR);
    if (*fd != -1)
        return SUCCESS;    

    *detailed_error_code = errno;
    return FAIL_OPEN_FILE;
}

PRIVATE int32_t
_read_file(int32_t fd, void *buffer, int64_t required_size, int64_t offset, int64_t *actual_size, int32_t *detailed_error_code)
{
    int32_t rc;
    int64_t remain_size = required_size;
    int64_t already_read;
    *actual_size = 0;
     while (remain_size > 0)
    {
        already_read = rvn_pread(fd, buffer, remain_size, offset);
        if (already_read == -1)
        {
            rc = FAIL_READ_FILE;
            goto error_cleanup;
        }
        if (already_read == 0)
        {
            rc = FAIL_EOF;
            goto error_cleanup;
        }

        remain_size -= already_read;
        buffer += already_read;
        offset += already_read;
    }

    *actual_size = required_size;
    return SUCCESS;

error_cleanup:
    *detailed_error_code = errno;
    *actual_size = required_size - remain_size; 
    return rc;
}

PRIVATE int32_t
_resize_file(int32_t fd, int64_t size, int32_t *detailed_error_code)
{
    assert(size % 4096 == 0);

    int32_t rc;
    struct stat st;
    if (fstat(fd, &st) == -1)
    {
        rc = FAIL_STAT_FILE;
        goto error_cleanup;
    }

    if(size > st.st_size)
    {
        int32_t rc = _allocate_file_space(fd, size, detailed_error_code);
        if(rc != SUCCESS)
            return rc;
    }
    else
    {
        if(rvn_ftruncate(fd, size) == -1)
        {
            rc = FAIL_TRUNCATE_FILE;
            goto error_cleanup;
        }
    }

    return SUCCESS;

error_cleanup:
    *detailed_error_code = errno;
    return rc;
}
