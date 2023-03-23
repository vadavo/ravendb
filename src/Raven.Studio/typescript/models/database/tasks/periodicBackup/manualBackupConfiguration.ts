﻿/// <reference path="../../../../../typings/tsd.d.ts"/>
import jsonUtil = require("common/jsonUtil");
import database = require("models/resources/database");
import backupConfiguration = require("models/database/tasks/periodicBackup/backupConfiguration");
import backupNowManualCommand = require("commands/database/tasks/backupNowManualCommand");
import notificationCenter = require("common/notifications/notificationCenter");

class manualBackupConfiguration extends backupConfiguration {

    backupOperation = "manual";

    constructor(databaseName: KnockoutObservable<string>,
                dto: Raven.Client.Documents.Operations.Backups.PeriodicBackupConfiguration,
                serverLimits: periodicBackupServerLimitsResponse,
                encryptedDatabase: boolean) {

        super(databaseName, dto, serverLimits, encryptedDatabase);

        this.initObservables();
        this.initValidation();
    }
    
    initObservables() {
        super.initObservables();

        this.dirtyFlag = new ko.DirtyFlag([
            this.backupType,
            this.encryptionSettings().dirtyFlag().isDirty,
            this.anyBackupTypeIsDirty
        ], false, jsonUtil.newLineNormalizingHashFunction);
    }

    initValidation() {
        super.initValidation();
        
        this.backupType.extend({
            required: true
        });

        this.validationGroup = ko.validatedObservable({
            backupType: this.backupType,
            hasDestination: this.hasDestination
        });
    }

    getTitleForView() {
        return "Create a Backup";
    }

    toDto(): Raven.Client.Documents.Operations.Backups.BackupConfiguration {
        return {
            BackupType: this.backupType(),
            SnapshotSettings: this.snapshot().toDto(),
            BackupEncryptionSettings: this.encryptionSettings().toDto(),
            LocalSettings: this.localSettings().toDto(),
            S3Settings: this.s3Settings().toDto(),
            GlacierSettings: this.glacierSettings().toDto(),
            AzureSettings: this.azureSettings().toDto(),
            GoogleCloudSettings: this.googleCloudSettings().toDto(),
            FtpSettings: this.ftpSettings().toDto()
        }
    }

    static empty(databaseName: KnockoutObservable<string>,
                 serverLimits: periodicBackupServerLimitsResponse,
                 encryptedDatabase: boolean) : manualBackupConfiguration {
        return new manualBackupConfiguration(databaseName, backupConfiguration.emptyDto(), serverLimits, encryptedDatabase);
    }
    
    submit(db: database, cfg: Raven.Client.Documents.Operations.Backups.BackupConfiguration) {
        return new backupNowManualCommand(db, cfg)
            .execute()
            .done((manualBackupResult: Raven.Client.Documents.Operations.Backups.StartBackupOperationResult) => {
                notificationCenter.instance.openDetailsForOperationById(db, manualBackupResult.OperationId);
            });
    }

    setState(): void {
        // empty
    }
}

export = manualBackupConfiguration;
