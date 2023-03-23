﻿/// <reference path="../../../../../typings/tsd.d.ts"/>
import jsonUtil = require("common/jsonUtil");
import database = require("models/resources/database");
import retentionPolicy = require("models/database/tasks/periodicBackup/retentionPolicy");
import backupConfiguration = require("models/database/tasks/periodicBackup/backupConfiguration");
import savePeriodicBackupConfigurationCommand = require("commands/database/tasks/savePeriodicBackupConfigurationCommand");

class periodicBackupConfiguration extends backupConfiguration {

    backupOperation = "periodic";

    name = ko.observable<string>();
    
    disabled = ko.observable<boolean>();
    stateText: KnockoutComputed<string>;

    manualChooseMentor = ko.observable<boolean>(false);
    pinMentorNode = ko.observable<boolean>(false);

    fullBackupEnabled = ko.observable<boolean>(false);
    fullBackupFrequency = ko.observable<string>();
    
    incrementalBackupEnabled = ko.observable<boolean>(false);
    incrementalBackupFrequency = ko.observable<string>();

    retentionPolicy = ko.observable<retentionPolicy>();

    constructor(databaseName: KnockoutObservable<string>,
                dto: Raven.Client.Documents.Operations.Backups.PeriodicBackupConfiguration |
                     Raven.Client.ServerWide.Operations.Configuration.ServerWideBackupConfiguration,
                serverLimits: periodicBackupServerLimitsResponse,
                encryptedDatabase: boolean,
                isServerWide: boolean) {
        
        super(databaseName, dto, serverLimits, encryptedDatabase, isServerWide);
       
        this.disabled(dto.Disabled);
        this.name(dto.Name);
        
        this.fullBackupEnabled(!!dto.FullBackupFrequency);
        this.fullBackupFrequency(dto.FullBackupFrequency || periodicBackupConfiguration.defaultFullBackupFrequency);
        this.incrementalBackupEnabled(!!dto.IncrementalBackupFrequency);
        this.incrementalBackupFrequency(dto.IncrementalBackupFrequency || periodicBackupConfiguration.defaultIncrementalBackupFrequency);
        
        this.manualChooseMentor(!!dto.MentorNode);
        this.pinMentorNode(dto.PinToMentorNode);
        
        this.retentionPolicy(!dto.RetentionPolicy ? retentionPolicy.empty() : new retentionPolicy(dto.RetentionPolicy));
       
        this.initObservables();
        this.initValidation();
    }
    
    initObservables() {
        super.initObservables();

        this.encryptionSettings.subscribe(() => {
           if (this.backupType() === "Snapshot" && this.encryptionSettings().enabled()) {
               this.encryptionSettings().mode("UseDatabaseKey");
           } 
        });

        this.stateText = ko.pureComputed(() => {
            if (this.disabled()) {
                return "Disabled";
            }

            return "Enabled";
        });

        this.dirtyFlag = new ko.DirtyFlag([
            this.name,
            this.disabled,
            this.backupType,
            this.fullBackupFrequency,
            this.fullBackupEnabled,
            this.incrementalBackupFrequency,
            this.incrementalBackupEnabled,
            this.manualChooseMentor,
            this.mentorNode,
            this.pinMentorNode,
            this.snapshot().compressionLevel,
            this.snapshot().excludeIndexes,
            this.retentionPolicy().dirtyFlag().isDirty,
            this.encryptionSettings().dirtyFlag().isDirty,
            this.anyBackupTypeIsDirty
        ], false, jsonUtil.newLineNormalizingHashFunction);
    }

    initValidation() {
        super.initValidation();
        
        this.backupType.extend({
            required: true
        });

        this.incrementalBackupEnabled.extend({
            validation: [
                {
                    validator: (e: boolean) => this.fullBackupEnabled() || e,
                    message: "Please select either full or incremental backup"
                }
            ]
        });
        
        this.fullBackupFrequency.extend({
            required: {
                onlyIf: () => this.fullBackupEnabled()
            }
        });
        
        this.incrementalBackupFrequency.extend({
            required: {
                onlyIf: () => this.incrementalBackupEnabled()
            }
        });
        
        this.mentorNode.extend({
            required: {
                onlyIf: () => this.manualChooseMentor()
            }
        });
        
        this.validationGroup = ko.validatedObservable({
            backupType: this.backupType,
            fullBackupFrequency: this.fullBackupFrequency,
            fullBackupEnabled: this.fullBackupEnabled,
            incrementalBackupFrequency: this.incrementalBackupFrequency,
            incrementalBackupEnabled: this.incrementalBackupEnabled,
            mentorNode: this.mentorNode,
            minimumBackupAgeToKeep: this.retentionPolicy().minimumBackupAgeToKeep,
            hasDestination: this.hasDestination
        });
    }

    getTitleForView(isNew: boolean) {
        return isNew ? "New Periodic Backup" : "Edit Periodic Backup";
    }

    getFullBackupFrequency() {
        return this.fullBackupFrequency;
    }

    getIncrementalBackupFrequency() {
        return this.incrementalBackupFrequency;
    }

    toDto(): Raven.Client.Documents.Operations.Backups.PeriodicBackupConfiguration {
        return {
            TaskId: this.taskId(),
            Name: this.name(),
            Disabled: this.disabled(),
            MentorNode: this.manualChooseMentor() ? this.mentorNode() : undefined,
            PinToMentorNode: this.pinMentorNode(),
            FullBackupFrequency: this.fullBackupEnabled() ? this.fullBackupFrequency() : null,
            IncrementalBackupFrequency: this.incrementalBackupEnabled() ? this.incrementalBackupFrequency() : null,
            RetentionPolicy: this.retentionPolicy().toDto(),
            BackupType: this.backupType(),
            SnapshotSettings: this.snapshot().toDto(),
            BackupEncryptionSettings: this.encryptionSettings().toDto(),
            LocalSettings: this.localSettings().toDto(),
            S3Settings: this.s3Settings().toDto(),
            GlacierSettings: this.glacierSettings().toDto(),
            AzureSettings: this.azureSettings().toDto(),
            GoogleCloudSettings: this.googleCloudSettings().toDto(),
            FtpSettings: this.ftpSettings().toDto()
        };
    }

    static empty(databaseName: KnockoutObservable<string>, 
                 serverLimits: periodicBackupServerLimitsResponse, 
                 encryptedDatabase: boolean, 
                 isServerWide: boolean): periodicBackupConfiguration {
        return new periodicBackupConfiguration(databaseName, backupConfiguration.emptyDto(), serverLimits, encryptedDatabase, isServerWide);
    }

    submit(db: database, cfg: Raven.Client.Documents.Operations.Backups.BackupConfiguration) {
        return new savePeriodicBackupConfigurationCommand(db, cfg as Raven.Client.Documents.Operations.Backups.PeriodicBackupConfiguration)
            .execute()
    }

    setState(state: Raven.Client.Documents.Operations.OngoingTasks.OngoingTaskState): void {
        this.disabled(state === "Disabled");
    }

    togglePinMentorNode() {
        this.pinMentorNode.toggle();
    }
}

export = periodicBackupConfiguration;
