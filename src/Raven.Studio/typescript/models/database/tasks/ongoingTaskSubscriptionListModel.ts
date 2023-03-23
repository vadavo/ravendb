﻿/// <reference path="../../../../typings/tsd.d.ts"/>
import appUrl = require("common/appUrl");
import generalUtils = require("common/generalUtils");
import ongoingTaskListModel = require("models/database/tasks/ongoingTaskListModel");
import ongoingTaskInfoCommand = require("commands/database/tasks/getOngoingTaskInfoCommand");
import subscriptionConnectionDetailsCommand = require("commands/database/tasks/getSubscriptionConnectionDetailsCommand");
import dropSubscriptionConnectionCommand = require("commands/database/tasks/dropSubscriptionConnectionCommand");
import activeDatabaseTracker = require("common/shell/activeDatabaseTracker");
import moment = require("moment");

type PerConnectionStats = {
    clientUri: string;
    workerId: string;
    strategy?: Raven.Client.Documents.Subscriptions.SubscriptionOpeningStrategy;
}

class ongoingTaskSubscriptionListModel extends ongoingTaskListModel {
    
    activeDatabase = activeDatabaseTracker.default.database;

    // General stats
    lastTimeServerMadeProgressWithDocuments = ko.observable<string>();
    lastClientConnectionTime = ko.observable<string>();
    changeVectorForNextBatchStartingPoint = ko.observable<string>(null);

    // Live connection stats
    clients = ko.observableArray<PerConnectionStats>([]);
    clientDetailsIssue = ko.observable<string>(); // null (ok) | client is not connected | failed to get details..
    subscriptionMode = ko.observable<string>();
    textClass = ko.observable<string>("text-details");

    validationGroup: KnockoutValidationGroup;
    
    constructor(dto: Raven.Client.Documents.Operations.OngoingTasks.OngoingTaskSubscription) {
        super();

        this.update(dto);
        this.initializeObservables(); 
        
        _.bindAll(this, "disconnectClientFromSubscription");
    }

    initializeObservables(): void {
        super.initializeObservables();

        const urls = appUrl.forCurrentDatabase();
        this.editUrl = urls.editSubscription(this.taskId, this.taskName());

        this.taskState.subscribe(() => this.refreshIfNeeded());
    }

    toggleDetails(): void {
        this.showDetails.toggle();
        this.refreshIfNeeded()
    }
    
    private refreshIfNeeded(): void {
        if (this.showDetails()) {
            this.refreshSubscriptionInfo();
        }
    }

    private refreshSubscriptionInfo() {
        // 1. Get general info
        ongoingTaskInfoCommand.forSubscription(this.activeDatabase(), this.taskId, this.taskName())
            .execute()
            .done((result: Raven.Client.Documents.Subscriptions.SubscriptionStateWithNodeDetails) => {

                this.responsibleNode(result.ResponsibleNode);
                this.taskState(result.Disabled ? 'Disabled' : 'Enabled');
                
                this.changeVectorForNextBatchStartingPoint(result.ChangeVectorForNextBatchStartingPoint);
                
                const dateFormat = generalUtils.dateFormat;

                const lastServerTime = (result.LastBatchAckTime) ? moment.utc(result.LastBatchAckTime).local().format(dateFormat):"N/A";
                this.lastTimeServerMadeProgressWithDocuments(lastServerTime);
                const lastClientTime = (result.LastClientConnectionTime)?moment.utc(result.LastClientConnectionTime).local().format(dateFormat):"N/A";
                this.lastClientConnectionTime(lastClientTime);

                // 2. Get connection details info
                this.clientDetailsIssue(null);
                new subscriptionConnectionDetailsCommand(this.activeDatabase(), this.taskId, this.taskName(), this.responsibleNode().NodeUrl)
                    .execute()
                    .done((result: Raven.Server.Documents.TcpHandlers.SubscriptionConnectionsDetails) => {

                        this.subscriptionMode(result.SubscriptionMode);
                        
                        this.clients(result.Results.map(x => ({
                            clientUri: x.ClientUri,
                            strategy: x.Strategy,
                            workerId: x.WorkerId
                        })));

                        if (!result.Results.length) { 
                            this.clientDetailsIssue("No client is connected");
                            this.textClass("text-warning");
                        }
                    })
                    .fail((response: JQueryXHR) => {
                        if (response.status === 0) {
                            // we can't even connect to node, show node connectivity error
                            this.clientDetailsIssue("Failed to connect to " + this.responsibleNode().NodeUrl + ". Please make sure this url is accessible from your browser.");
                        } else {
                            this.clientDetailsIssue("Failed to get client connection details");
                        }
                        
                        this.textClass("text-danger");
                    });
            });
    }

    disconnectClientFromSubscription(workerId: string) {
        new dropSubscriptionConnectionCommand(this.activeDatabase(), this.taskId, this.taskName(), workerId)
            .execute()
            .done(() => this.refreshSubscriptionInfo());
    }
}

export = ongoingTaskSubscriptionListModel;
