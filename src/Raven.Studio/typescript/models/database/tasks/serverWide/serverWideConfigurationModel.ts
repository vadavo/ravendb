﻿/// <reference path="../../../../../typings/tsd.d.ts"/>
import serverWideExcludeModel = require("models/database/tasks/serverWide/serverWideExcludeModel");

abstract class serverWideConfigurationModel {
    taskId = ko.observable<number>();
    taskName = ko.observable<string>();
    
    disabled = ko.observable<boolean>();
    stateText: KnockoutComputed<string>;
    
    mentorNode = ko.observable<string>();
    pinMentorNode = ko.observable<boolean>(false);
    manualChooseMentor = ko.observable<boolean>(false);
    
    excludeInfo = ko.observable<serverWideExcludeModel>();

    constructor(taskId: number, taskName: string, disabled: boolean, mentorNode: string, pinMentorNode: boolean, excludedDatabases: string[]) {
        this.taskId(taskId);
        this.taskName(taskName);
        
        this.mentorNode(mentorNode);
        this.pinMentorNode(pinMentorNode);
        
        this.disabled(disabled);

        this.manualChooseMentor(!!mentorNode);
        
        this.excludeInfo(new serverWideExcludeModel(excludedDatabases));

        this.mentorNode.extend({
            required: {
                onlyIf: () => this.manualChooseMentor()
            }
        });

        this.stateText = ko.pureComputed(() => {
            if (this.disabled()) {
                return "Disabled";
            }

            return "Enabled";
        });
    }

    togglePinMentorNode() {
        this.pinMentorNode.toggle();
    }
}

export = serverWideConfigurationModel;
