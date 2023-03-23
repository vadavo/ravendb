﻿/// <reference path="../../../../../typings/tsd.d.ts"/>
import serverWideConfigurationModel = require("models/database/tasks/serverWide/serverWideConfigurationModel");
import connectionStringRavenEtlModel = require("models/database/settings/connectionStringRavenEtlModel");
import generalUtils = require("common/generalUtils");
import jsonUtil = require("common/jsonUtil");

class serverWideExternalReplicationEditModel extends serverWideConfigurationModel {
    delayTime = ko.observable<number>();
    showDelayTime = ko.observable<boolean>(false);
    humaneDelayDescription: KnockoutComputed<string>;
    
    connectionString = ko.observable<connectionStringRavenEtlModel>();
    
    dirtyFlag: () => DirtyFlag;
    validationGroup: KnockoutValidationGroup;

    constructor(dto: Raven.Client.ServerWide.Operations.OngoingTasks.ServerWideExternalReplication) {
        super(dto.TaskId, dto.Name, dto.Disabled, dto.MentorNode, dto.PinToMentorNode, dto.ExcludedDatabases);

        const delayTime = generalUtils.timeSpanToSeconds(dto.DelayReplicationFor);
        this.showDelayTime(dto.DelayReplicationFor != null && delayTime !== 0);
        this.delayTime(dto.DelayReplicationFor ? delayTime : null);
       
        const connectionStringObject: Raven.Client.Documents.Operations.ETL.RavenConnectionString = {
            Database: "server-wide",
            TopologyDiscoveryUrls: dto.TopologyDiscoveryUrls,
            Type: "Raven",
            Name: undefined
        };
        this.connectionString(new connectionStringRavenEtlModel(connectionStringObject, false, []))
        
        this.initObservables();
        this.initValidation();
    }

    private initObservables() {
        this.humaneDelayDescription = ko.pureComputed(() => {
            const delayTimeHumane = generalUtils.formatTimeSpan(this.delayTime() * 1000, true);
            return this.showDelayTime() && this.delayTime.isValid() && this.delayTime() !== 0 ?
                `Documents will be replicated after a delay time of <strong>${delayTimeHumane}</strong>` : "";
        });
        
        this.dirtyFlag = new ko.DirtyFlag([
            this.taskName,
            this.disabled,
            this.delayTime,
            this.showDelayTime,
            this.manualChooseMentor,
            this.mentorNode,
            this.pinMentorNode,
            this.excludeInfo().dirtyFlag().isDirty,
            this.connectionString().dirtyFlag().isDirty
        ], false, jsonUtil.newLineNormalizingHashFunction);
    }

    private initValidation() {
        this.delayTime.extend({
            validation: [
                {
                    validator: () => !this.showDelayTime() || !!this.delayTime(),
                    message: "Please enter a value greater than 0"
                }
            ],
            min: 0
        });
        
        this.validationGroup = ko.validatedObservable({
            delayTime: this.delayTime,
            mentorNode: this.mentorNode,
            databasesToExclude: this.excludeInfo().databasesToExclude,
            topologyDiscoveryUrls: this.connectionString().topologyDiscoveryUrls
        });
    }

    toDto(): Raven.Client.ServerWide.Operations.OngoingTasks.ServerWideExternalReplication {
        return {
            TaskId: this.taskId(),
            Name: this.taskName(),
            DelayReplicationFor: this.showDelayTime() ? generalUtils.formatAsTimeSpan(this.delayTime() * 1000) : null,
            TopologyDiscoveryUrls: this.connectionString().topologyDiscoveryUrls().map(x => x.discoveryUrlName()),
            ExcludedDatabases: this.excludeInfo().toDto(),
            MentorNode: this.manualChooseMentor() ? this.mentorNode() : undefined,
            PinToMentorNode: this.pinMentorNode(),
            Disabled: this.disabled()
        };
    }

    static empty(): serverWideExternalReplicationEditModel {
        return new serverWideExternalReplicationEditModel({
            TaskId: 0,
            Name: null,
            DelayReplicationFor: null,
            TopologyDiscoveryUrls: [],
            ExcludedDatabases: [],
            MentorNode: null,
            PinToMentorNode: false,
            Disabled: false,
        });
    }
}

export = serverWideExternalReplicationEditModel;
