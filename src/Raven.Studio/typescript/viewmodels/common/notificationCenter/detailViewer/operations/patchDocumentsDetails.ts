import app = require("durandal/app");

import database = require("models/resources/database");
import operation = require("common/notifications/models/operation");
import abstractNotification = require("common/notifications/models/abstractNotification");
import notificationCenter = require("common/notifications/notificationCenter");
import virtualUpdateByQuery = require("common/notifications/models/virtualUpdateByQuery");
import virtualUpdateByQueryFailures = require("common/notifications/models/virtualUpdateByQueryFailures");
import abstractOperationDetails = require("viewmodels/common/notificationCenter/detailViewer/operations/abstractOperationDetails");

class patchDocumentsDetails extends abstractOperationDetails {

    view = require("views/common/notificationCenter/detailViewer/operations/patchDocumentsDetails.html");

    progress: KnockoutObservable<Raven.Client.Documents.Operations.DeterminateProgress>;
    result: KnockoutObservable<Raven.Client.Documents.Operations.BulkOperationResult>;
    processingSpeed: KnockoutObservable<string>;
    estimatedTimeLeft: KnockoutObservable<string>;
    
    query: string;

    constructor(op: operation, notificationCenter: notificationCenter) {
        super(op, notificationCenter);

        this.initObservables();
    }

    initObservables() {
        super.initObservables();
        
        this.query = (this.op.detailedDescription() as Raven.Client.Documents.Operations.BulkOperationResult.OperationDetails).Query;

        this.progress = ko.pureComputed(() => {
            return this.op.progress() as Raven.Client.Documents.Operations.DeterminateProgress;
        });

        this.result = ko.pureComputed(() => {
            return this.op.status() === "Completed" ? this.op.result() as Raven.Client.Documents.Operations.BulkOperationResult : null;
        });

        this.processingSpeed = ko.pureComputed(() => {
            const progress = this.progress();
            if (!progress) {
                return "N/A";
            }

            const processingSpeed = this.calculateProcessingSpeed(progress.Processed);
            if (processingSpeed === 0) {
                return "N/A";
            }

            return `${processingSpeed.toLocaleString()} docs / sec`;
        }).extend({ rateLimit : 2000 });

        this.estimatedTimeLeft = ko.pureComputed(() => {
            const progress = this.progress();
            if (!progress) {
                return "N/A";
            }
            return this.getEstimatedTimeLeftFormatted(progress.Processed, progress.Total);
        }).extend({ rateLimit : 2000 });
    }

    static tryHandle(operationDto: Raven.Server.NotificationCenter.Notifications.OperationChanged, notificationsContainer: KnockoutObservableArray<abstractNotification>,
                     database: database, callbacks: { spinnersCleanup: () => void, onChange: () => void }): boolean {

        if (operationDto.Type === "OperationChanged" && operationDto.TaskType === "UpdateByQuery") {
            if (operationDto.State.Status === "Completed") {
                abstractOperationDetails.handleInternal(virtualUpdateByQuery, operationDto, notificationsContainer, database, callbacks);
                return true;
            }
            
            if (operationDto.State.Status === "Faulted") {
                abstractOperationDetails.handleInternal(virtualUpdateByQueryFailures, operationDto, notificationsContainer, database, callbacks);
                return true;
            }
        }

        return false;
    }
    
    static supportsDetailsFor(notification: abstractNotification) {
        return (notification instanceof operation) && (notification.taskType() === "UpdateByQuery");
    }

    static showDetailsFor(op: operation, center: notificationCenter) {
        return app.showBootstrapDialog(new patchDocumentsDetails(op, center));
    }

}

export = patchDocumentsDetails;
