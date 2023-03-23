﻿/// <reference path="../../../../../typings/tsd.d.ts"/>
import localSettings = require("models/database/tasks/periodicBackup/localSettings");
import s3Settings = require("viewmodels/database/tasks/destinations/s3Settings");
import glacierSettings = require("viewmodels/database/tasks/destinations/glacierSettings");
import azureSettings = require("viewmodels/database/tasks/destinations/azureSettings");
import googleCloudSettings = require("viewmodels/database/tasks/destinations/googleCloudSettings");
import ftpSettings = require("viewmodels/database/tasks/destinations/ftpSettings");
import getBackupLocationCommand = require("commands/database/tasks/getBackupLocationCommand");
import getServerWideBackupLocationCommand = require("commands/serverWide/tasks/getServerWideBackupLocationCommand");
import getFolderPathOptionsCommand = require("commands/resources/getFolderPathOptionsCommand");
import activeDatabaseTracker = require("common/shell/activeDatabaseTracker");
import snapshot = require("models/database/tasks/periodicBackup/snapshot");
import encryptionSettings = require("models/database/tasks/periodicBackup/encryptionSettings");
import generalUtils = require("common/generalUtils");
import backupSettings = require("models/database/tasks/periodicBackup/backupSettings");

abstract class backupConfiguration {

    static readonly defaultFullBackupFrequency = "0 2 * * 0";
    static readonly defaultIncrementalBackupFrequency = "0 2 * * *";
    
    taskId = ko.observable<number>();
    isManualBackup = ko.observable<boolean>(false);
    isServerWide = ko.observable<boolean>();
    
    backupType = ko.observable<Raven.Client.Documents.Operations.Backups.BackupType>();
    isSnapshot = ko.pureComputed(() => this.backupType() === "Snapshot");
    backupOptions = ["Backup", "Snapshot"];
    anyBackupTypeIsDirty: KnockoutComputed<boolean>;
    snapshot = ko.observable<snapshot>();
    
    mentorNode = ko.observable<string>();
    
    encryptionSettings = ko.observable<encryptionSettings>();
    
    localSettings = ko.observable<localSettings>();
    s3Settings = ko.observable<s3Settings>();
    glacierSettings = ko.observable<glacierSettings>();
    azureSettings = ko.observable<azureSettings>();
    googleCloudSettings = ko.observable<googleCloudSettings>();
    ftpSettings = ko.observable<ftpSettings>();
    
    hasDestination: KnockoutComputed<boolean>;
    validationGroup: KnockoutValidationGroup;
    
    locationInfo = ko.observableArray<Raven.Server.Web.Studio.SingleNodeDataDirectoryResult>([]);
    folderPathOptions = ko.observableArray<string>([]);

    spinners = {
        locationInfoLoading: ko.observable<boolean>(false)
    };

    dirtyFlag: () => DirtyFlag;

    protected constructor(private databaseName: KnockoutObservable<string>,
                dto: Raven.Client.Documents.Operations.Backups.PeriodicBackupConfiguration |
                     Raven.Client.ServerWide.Operations.Configuration.ServerWideBackupConfiguration,
                serverLimits: periodicBackupServerLimitsResponse,
                encryptedDatabase: boolean,
                isServerWide = false) {
        this.taskId(dto.TaskId);
        this.backupType(dto.BackupType);
        this.localSettings(!dto.LocalSettings ? localSettings.empty("backup") : new localSettings(dto.LocalSettings, "backup"));
        this.s3Settings(!dto.S3Settings ? s3Settings.empty(serverLimits.AllowedAwsRegions, "Backup") : new s3Settings(dto.S3Settings, serverLimits.AllowedAwsRegions, "Backup"));
        this.azureSettings(!dto.AzureSettings ? azureSettings.empty("Backup") : new azureSettings(dto.AzureSettings, "Backup"));
        this.googleCloudSettings(!dto.GoogleCloudSettings ? googleCloudSettings.empty("Backup") : new googleCloudSettings(dto.GoogleCloudSettings, "Backup"));
        this.glacierSettings(!dto.GlacierSettings ? glacierSettings.empty(serverLimits.AllowedAwsRegions, "Backup") : new glacierSettings(dto.GlacierSettings, serverLimits.AllowedAwsRegions, "Backup"));
        this.ftpSettings(!dto.FtpSettings ? ftpSettings.empty("Backup") : new ftpSettings(dto.FtpSettings, "Backup"));
        this.isServerWide(isServerWide);
        
        this.mentorNode(dto.MentorNode);

        const folderPath = this.localSettings().folderPath();
        if (folderPath) {
            this.updateLocationInfo(folderPath);
        }

        this.updateFolderPathOptions(folderPath);

        this.snapshot(!dto.SnapshotSettings ? snapshot.empty() : new snapshot(dto.SnapshotSettings));
        
        this.encryptionSettings(new encryptionSettings(this.databaseName, encryptedDatabase, this.backupType, dto.BackupEncryptionSettings, this.isServerWide()));
    }
    
    initObservables() {
        this.anyBackupTypeIsDirty = ko.pureComputed(() => {
            let anyDirty = false;
            const backupTypes: backupSettings[] = [this.localSettings(), this.s3Settings(), this.glacierSettings(), this.azureSettings(), this.googleCloudSettings(), this.ftpSettings()];

            backupTypes.forEach(type => {
                if (type.dirtyFlag().isDirty()) {
                    anyDirty = true;
                }
            });

            return anyDirty;
        });
        
        this.localSettings().folderPath.throttle(300).subscribe((newPathValue) => {
            if (this.localSettings().folderPath.isValid()) {
                this.updateLocationInfo(newPathValue);
                this.updateFolderPathOptions(newPathValue);
            } else {
                this.locationInfo([]);
                this.folderPathOptions([]);
                this.spinners.locationInfoLoading(false);
            }
        });

        this.hasDestination = ko.pureComputed(() => {
            return this.localSettings().enabled() ||
                this.s3Settings().enabled() ||
                this.glacierSettings().enabled() ||
                this.azureSettings().enabled() ||
                this.googleCloudSettings().enabled() ||
                this.ftpSettings().enabled();
        })
    }

    initValidation() {
        this.backupType.extend({
            required: true
        });

        this.hasDestination.extend({
            validation: [
                {
                    validator: () => this.hasDestination(),
                    message: "No destination is defined"
                }
            ]
        });
    }

    getFullBackupFrequency() {
        return ko.observable(backupConfiguration.defaultFullBackupFrequency);
    }

    getIncrementalBackupFrequency() {
        return ko.observable(backupConfiguration.defaultIncrementalBackupFrequency);
    }
    
    private updateLocationInfo(path: string) {
        const getLocationCommand = this.isServerWide() ? 
                        new getServerWideBackupLocationCommand(path) : 
                        new getBackupLocationCommand(path, activeDatabaseTracker.default.database());

        const getLocationtask = getLocationCommand
            .execute()
            .done((result: Raven.Server.Web.Studio.DataDirectoryResult) => {
                if (this.localSettings().folderPath() !== path) {
                    // the path has changed
                    return;
                }

                this.locationInfo(result.List);
            });
        
        generalUtils.delayedSpinner(this.spinners.locationInfoLoading, getLocationtask);
    }

    private updateFolderPathOptions(path: string) {
        getFolderPathOptionsCommand.forServerLocal(path, true, this.databaseName() ? activeDatabaseTracker.default.database() : null)
            .execute()
            .done((result: Raven.Server.Web.Studio.FolderPathOptions) => {
                if (this.localSettings().folderPath() !== path) {
                    // the path has changed
                    return;
                }

                this.folderPathOptions(result.List);
            });
    }

    useBackupType(backupType: Raven.Client.Documents.Operations.Backups.BackupType) {
        this.backupType(backupType);
    }

    getPathForCreatedBackups(backupLocationInfo: Raven.Server.Web.Studio.SingleNodeDataDirectoryResult) {
        return ko.pureComputed(() => {
            const separator = backupLocationInfo.FullPath[0] === "/" ? "/" : "\\";
            
            return this.isServerWide() ? 
                `${backupLocationInfo.FullPath}${separator}{DATABASE-NAME}${separator}` :
                `${backupLocationInfo.FullPath}`; 
        })
    }

    static emptyDto(): Raven.Client.Documents.Operations.Backups.PeriodicBackupConfiguration {
        return {
            TaskId: 0,
            Disabled: false,
            Name: null,
            BackupType: null,
            FullBackupFrequency: null,
            IncrementalBackupFrequency: null,
            LocalSettings: null,
            S3Settings: null,
            GlacierSettings: null,
            AzureSettings: null,
            GoogleCloudSettings: null,
            FtpSettings: null,
            MentorNode: null,
            PinToMentorNode: false,
            BackupEncryptionSettings: null,
            SnapshotSettings: null,
            RetentionPolicy: null,
        }
    }
}

export = backupConfiguration;
