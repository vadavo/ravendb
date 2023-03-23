/// <reference path="../../../../typings/tsd.d.ts"/>
import genericProgress = require("common/helpers/database/genericProgress");
import serverTime = require("common/helpers/database/serverTime");
import timeHelpers = require("common/timeHelpers");
import generalUtils = require("common/generalUtils");
import moment = require("moment");

class progress extends genericProgress {
    
    isStale = ko.observable<boolean>();
    indexRunningStatus = ko.observable<Raven.Client.Documents.Indexes.IndexRunningStatus>();
    
    isDisabled: KnockoutComputed<boolean>;

    constructor(processed: number,
                total: number,
                numberFormatter: (number: number) => string,
                processedPerSecond = 0,
                isStale = false,
                indexRunningStatus: Raven.Client.Documents.Indexes.IndexRunningStatus = "Running") {
        super(processed, total, numberFormatter, processedPerSecond);
        
        this.isStale(isStale);
        this.indexRunningStatus(indexRunningStatus);
        
        this.isDisabled = ko.pureComputed(() => {
            const status = this.indexRunningStatus();
            return status === "Disabled" || status === "Paused";
        });
        
        this.completed = ko.pureComputed(() => {
            const processed = this.processed();
            const total = this.total();
            const stale = this.isStale();
            
            return processed === total && !stale;
        });
        
        this.percentage = ko.pureComputed(() => {
            const percentage = this.defaultPercentage();
            return percentage === 100 && this.isStale() ? 99.9 : percentage;
        });
        
        this.formattedTimeLeftToProcess = ko.pureComputed(() => {
            if (this.isDisabled()) {
                return "Overall progress";
            }
            if (this.completed()) {
                return "Indexing completed";
            }
            return this.defaultFormattedTimeLeftToProcess();
        });
        
        this.textualProgress = ko.pureComputed(() => {
            if (this.total() === this.processed() && this.isStale()) {
                return "Processed all documents and tombstones, finalizing";
            }
            return this.defaultTextualProgress();
        })
    }

    markCompleted() {
        this.processed(this.total());
        this.isStale(false);
    }

    protected getDefaultTimeLeftMessage() {
        if (this.total() === this.processed() && this.isStale()) {
            return "Processed all documents and tombstones, finalizing";
        }

        let message: string;
        if (this.isDisabled()) {
            message = `Index is ${this.indexRunningStatus()}`;
        } else {
            message = "Overall progress";
        }

        return message;
    }
    
    updateWith(incomingProgress: progress) {
        this.processed(incomingProgress.processed());
        this.total(incomingProgress.total());
        this.processedPerSecond(incomingProgress.processedPerSecond());
        this.isStale(incomingProgress.isStale());
        this.indexRunningStatus(incomingProgress.indexRunningStatus());
    }
}

class collectionProgress {

    name: string;
    documentsProgress: progress;
    tombstonesProgress: progress;

    constructor(name: string, collectionStats: Raven.Client.Documents.Indexes.IndexProgress.CollectionStats, runningStatus: Raven.Client.Documents.Indexes.IndexRunningStatus) {
        this.name = name;
        this.documentsProgress = new progress(
            collectionStats.TotalNumberOfItems - collectionStats.NumberOfItemsToProcess,
            collectionStats.TotalNumberOfItems,
            collectionProgress.docsFormatter, 
            0, false, runningStatus);
        this.tombstonesProgress = new progress(
            collectionStats.TotalNumberOfTombstones - collectionStats.NumberOfTombstonesToProcess,
            collectionStats.TotalNumberOfTombstones,
            collectionProgress.docsFormatter,
            0, false, runningStatus);
    }

    static docsFormatter(processed: number): string {
        return processed.toLocaleString();
    }
}

class rollingProgress {
    nodeTag: string;
    state = ko.observable<Raven.Client.Documents.Indexes.RollingIndexState>();
    created = ko.observable<Date>();
    started = ko.observable<Date>();
    finished = ko.observable<Date>();
    
    elapsedText: KnockoutComputed<string>;
    finishedText: KnockoutComputed<string>;
    
    constructor(nodeTag: string, deployment: Raven.Client.Documents.Indexes.RollingIndexDeployment) {
        this.nodeTag = nodeTag;
        this.state(deployment.State);
        this.created(moment.utc(deployment.CreatedAt).toDate());
        this.started(deployment.StartedAt ? moment.utc(deployment.StartedAt).toDate() : null);
        this.finished(deployment.FinishedAt ? moment.utc(deployment.FinishedAt).toDate() : null);
        
        this.elapsedText = ko.pureComputed(() => {
            const end = this.finished() ? moment.utc(this.finished()) : null;
            const endTime = end || serverTime.default.getAdjustedTime(timeHelpers.utcNowWithSecondPrecision());

            const diff = Math.max(endTime.diff(this.started()), 0);
            const duration = moment.duration({
                milliseconds: diff
            });
            return generalUtils.formatDuration(duration, true, 2, true);
        });
        
        this.finishedText = ko.pureComputed(() => {
            const end = this.finished() ? moment.utc(this.finished()) : null;
            const endTime = end || serverTime.default.getAdjustedTime(timeHelpers.utcNowWithSecondPrecision());

            const diff = Math.max(endTime.diff(serverTime.default.getAdjustedTime(moment.utc())), 0);
            const duration = moment.duration({
                milliseconds: diff
            });
            return generalUtils.formatDuration(duration, true, 2, true);
        });
    }

    updateWith(incomingRolling: rollingProgress) {
        this.state(incomingRolling.state());
        this.created(incomingRolling.created());
        this.started(incomingRolling.started());
        this.finished(incomingRolling.finished());
    }

    markCompleted() {
        if (this.state() !== "Done") {
            this.state("Done");
            
            // we use approximate date here - server already cleaned up information about progress
            this.finished(new Date());
        }
    }
}

class indexProgress {

    collections = ko.observableArray<collectionProgress>();
    rollingProgress = ko.observableArray<rollingProgress>();
    globalProgress = ko.observable<progress>();
    isStale: boolean;
    name: string;
    canSwitchToParallelMode: KnockoutComputed<boolean>;

    constructor(dto: Raven.Client.Documents.Indexes.IndexProgress) {
        this.isStale = dto.IsStale;
        this.name = dto.Name;
        this.collections(_.map(dto.Collections, (value, key) => new collectionProgress(key, value, dto.IndexRunningStatus)));
        const rolling: rollingProgress[] = dto.IndexRollingStatus ? _.map(dto.IndexRollingStatus.ActiveDeployments, (value, key) => new rollingProgress(key, value)) : [];
        this.rollingProgress(rolling.reverse());
        
        const total = _.reduce(this.collections(), (p, c) => {
            return p + c.documentsProgress.total() + c.tombstonesProgress.total();
        }, 0);

        const processed = _.reduce(this.collections(), (p, c) => {
            return p + c.documentsProgress.processed() + c.tombstonesProgress.processed();
        }, 0);
        
        const units = dto.SourceType === "TimeSeries" || dto.SourceType === "Counters" ? "items" : "docs";
        this.globalProgress(new progress(
            processed, total, (processed: number) => `${processed.toLocaleString()} ${units}`,
            dto.ProcessedPerSecond, dto.IsStale, dto.IndexRunningStatus));
        
        this.canSwitchToParallelMode = ko.pureComputed(() => {
            const progress = this.rollingProgress();
            if (!progress) {
                return false;
            }
            
            // we allow to switch to parallel mode only if at least 2 nodes 
            // are not done
            return progress.filter(x => x.state() !== "Done").length > 1;
        });
    }
    
    public markCompleted() {
        this.globalProgress().markCompleted();
        
        this.collections().forEach(c => {
            c.documentsProgress.markCompleted();
            c.tombstonesProgress.markCompleted();
        });
        
        this.rollingProgress().forEach(r => r.markCompleted());
    }

    public updateProgress(incomingProgress: indexProgress) {
        this.globalProgress().updateWith(incomingProgress.globalProgress());
        
        const incomingCollections = incomingProgress.collections().map(x => x.name);
        incomingCollections.sort();

        const localCollections = this.collections().map(x => x.name);
        localCollections.sort();

        if (_.isEqual(incomingCollections, localCollections)) {
            // looks like collection names didn't change - let's update values 'in situ'

            this.collections().forEach(collection => {
                const collectionName = collection.name;

                const newObject = incomingProgress.collections().find(x => x.name === collectionName);
                collection.documentsProgress.updateWith(newObject.documentsProgress);
                collection.tombstonesProgress.updateWith(newObject.tombstonesProgress);
            })
        } else {
            // have we don't call updateWith on each collection to avoid animations
            this.collections(incomingProgress.collections());
        }
        
        const incomingRolling = incomingProgress.rollingProgress().map(x => x.nodeTag);
        incomingRolling.sort();
        
        const localRolling = this.rollingProgress().map(x => x.nodeTag);
        localRolling.sort();
        
        if (_.isEqual(incomingRolling, localRolling)) {
            // looks like rolling names didn't change - let's update values 'in situ'
            
            this.rollingProgress().forEach(rolling => {
                const nodeTag = rolling.nodeTag;
                
                const newObject = incomingProgress.rollingProgress().find(x => x.nodeTag === nodeTag);
                rolling.updateWith(newObject);
            })
        } else {
            // node tag has changed - quite rare case but let's update entire collection
            this.rollingProgress(incomingProgress.rollingProgress().reverse());
        }
    }
}

export = indexProgress; 
