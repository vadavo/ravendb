﻿using System;
using System.IO;
using System.Runtime.InteropServices;
using Sparrow.Platform;
using Voron.Platform.Posix;

namespace Voron.Util.Settings
{
    public abstract class PathSettingBase<T>
    {
        protected readonly PathSettingBase<T> _baseDataDir;
        protected readonly string _path;

        protected string _fullPath;

        protected PathSettingBase(string path, PathSettingBase<T> baseDataDir = null)
        {
            ValidatePath(path);
            _baseDataDir = baseDataDir;
            _path = path;
        }

        public static void ValidatePath(string path)
        {
            if (path!= null && 
                (path.StartsWith("appdrive:", StringComparison.OrdinalIgnoreCase) || 
                path.StartsWith("~") || 
                path.StartsWith("$home", StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException($"The path '{path}' is illegal! Paths in RavenDB can't start with 'appdrive:', '~' or '$home'");
            }
        }

        public string FullPath => _fullPath ?? (_fullPath = ToFullPath());

        public abstract T Combine(string path);

        public abstract T Combine(T path);

        public string ToFullPath()
        {
            return PathUtil.ToFullPath(_path, _baseDataDir?.FullPath);
        }

        public override string ToString()
        {
            return FullPath;
        }

        protected bool Equals(PathSettingBase<T> other)
        {
            return FullPath == other.FullPath;
        }

        public override bool Equals(object obj)
        {
            if (ReferenceEquals(null, obj)) return false;
            if (ReferenceEquals(this, obj)) return true;
            if (obj.GetType() != GetType()) return false;
            return Equals((PathSettingBase<T>)obj);
        }

        public override int GetHashCode()
        {
            return FullPath.GetHashCode();
        }
    }
    
    public class PathUtil
    {
        public static string ToFullPath(string inputPath, string baseDataDirFullPath = null)
        {
            var path = Environment.ExpandEnvironmentVariables(inputPath);

            if (PlatformDetails.RunningOnPosix == false && path.StartsWith(@"\") == false ||
                PlatformDetails.RunningOnPosix && path.StartsWith(@"/") == false) // if relative path
                path = Path.Combine(baseDataDirFullPath ?? AppContext.BaseDirectory, path);

            var result = Path.IsPathRooted(path)
                ? path
                : Path.Combine(baseDataDirFullPath ?? AppContext.BaseDirectory, path);

            if (result.Length >= 260 && 
                RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
                result.StartsWith(@"\\?\") == false)
                result = @"\\?\" + result;

            var resultRoot = Path.GetPathRoot(result);
            if (resultRoot != result && (result.EndsWith(@"\") || result.EndsWith("/")))
                result = result.TrimEnd('\\', '/');

            if (PlatformDetails.RunningOnPosix)
                result = PosixHelper.FixLinuxPath(result);

            return result != string.Empty || resultRoot == null ? 
                Path.GetFullPath(result) :
                Path.GetFullPath(resultRoot); // it will unify directory separators and sort out parent directories
        }

        public static bool IsSubDirectory(string userPath, string rootPath)
        {
            var rootDirInfo = new DirectoryInfo(rootPath);
            var userDirInfo = new DirectoryInfo(userPath);

            var comparisonType = PlatformDetails.RunningOnPosix ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            
            if (string.Equals(userDirInfo.FullName, rootDirInfo.FullName, comparisonType))
                return true;

            while (userDirInfo.Parent != null)
            {
                if (string.Equals(userDirInfo.Parent.FullName, rootDirInfo.FullName, comparisonType))
                {
                    return true;
                }

                userDirInfo = userDirInfo.Parent;
            }
            return false;
        }
    }
}
