/// <reference path="../../../../typings/tsd.d.ts"/>
import driveUsageDetails = require("models/resources/serverDashboard/driveUsageDetails");
import virtualGridController = require("widgets/virtualGrid/virtualGridController");
import hyperlinkColumn = require("widgets/virtualGrid/columns/hyperlinkColumn");
import textColumn = require("widgets/virtualGrid/columns/textColumn");
import generalUtils = require("common/generalUtils");
import appUrl = require("common/appUrl");
import virtualColumn = require("widgets/virtualGrid/columns/virtualColumn");
import databasesManager = require("common/shell/databasesManager");

class legendColumn<T extends object> implements virtualColumn {
    constructor(
        protected gridController: virtualGridController<T>,
        public colorClassAccessor: (item: T) => string,
        public header: string,
        public width: string) {
    }
    
    canHandle(): boolean {
        return false;
    }

    get headerTitle() {
        return this.header;
    }

    get headerAsText() {
        return this.header;
    }

    get sortable() {
        return false;
    }

    renderCell(item: T): string {
        const color = this.colorClassAccessor(item);
        return `<div class="cell text-cell" style="width: ${this.width}"><div class="legend-rect ${color}"></div></div>`;
    }
    
    toDto(): virtualColumnDto {
        throw new Error("Legend column does not support serialization");
    }
}

class driveUsage {
    private sizeFormatter = generalUtils.formatBytesToSize;
    
    private includeTemp: KnockoutObservable<boolean>;
    
    mountPoint = ko.observable<string>();
    volumeLabel = ko.observable<string>();
    totalCapacity = ko.observable<number>(0);
    freeSpace = ko.observable<number>(0);
    isLowSpace = ko.observable<boolean>();
    
    usedSpace: KnockoutComputed<number>;
    
    items = ko.observableArray<driveUsageDetails>([]);
    totalDocumentsSpaceUsed: KnockoutComputed<number>;
    mountPointLabel: KnockoutComputed<string>;
    
    usedSpacePercentage: KnockoutComputed<number>;
    ravendbToUsedSpacePercentage: KnockoutComputed<number>;

    gridController = ko.observable<virtualGridController<driveUsageDetails>>();
    private colorClassProvider: (name: string) => string;
    
    constructor(dto: Raven.Server.Dashboard.MountPointUsage, colorClassProvider: (name: string) => string, includeTemp: KnockoutObservable<boolean>) {
        this.colorClassProvider = colorClassProvider;
        this.includeTemp = includeTemp;
        this.update(dto);
        
        this.usedSpacePercentage = ko.pureComputed(() => {
            const total = this.totalCapacity();
            const used = this.usedSpace();
            
            if (!total) {
                return 0;
            }
            
            return used * 100.0 / total;
        });
        
        this.usedSpace = ko.pureComputed(() => {
            const total = this.totalCapacity();
            const free = this.freeSpace();
            return total - free;
        });

        this.totalDocumentsSpaceUsed = ko.pureComputed(() => {
            const includeTemp = this.includeTemp();
            return _.sum(this.items().map(x => includeTemp ? x.size() + x.tempBuffersSize() : x.size()));
        });

        this.ravendbToUsedSpacePercentage = ko.pureComputed(() => {
            const totalUsed = this.usedSpace();
            const documentsUsed = this.totalDocumentsSpaceUsed();
            
            if (!totalUsed) {
                return 0;
            }
            
            return documentsUsed *  100.0 / totalUsed;
        });
        
        const isDisabled = (dbName: string) => {
            const db = databasesManager.default.getDatabaseByName(dbName);
            if (db) {
                return db.disabled();
            }
            return false;
        };
        
        const gridInitialization = this.gridController.subscribe((grid) => {
            grid.headerVisible(true);
            grid.setDefaultSortBy(1);

            grid.init(() => $.when({
                totalResultCount: this.items().length,
                items: this.items()
            }), () => {
                if (this.includeTemp()) {
                    return [
                        new legendColumn<driveUsageDetails>(grid, x => this.colorClassProvider(x.database()), "", "26px"),
                        new hyperlinkColumn<driveUsageDetails>(grid, x => x.database(), x => this.getStorageReportUrl(x.database()), "Database", "42%", {
                            extraClass: d => isDisabled(d.database()) ? "disabled" : "",
                            sortable: "string",
                            customComparator: generalUtils.sortAlphaNumeric
                        }),
                        new textColumn<driveUsageDetails>(grid, x => this.sizeFormatter(x.size()), "Data", "16%", {
                            sortable: x => x.size(),
                            defaultSortOrder: "desc"
                        }),
                        new textColumn<driveUsageDetails>(grid, x => this.sizeFormatter(x.tempBuffersSize()), "Temp", "16%", {
                            sortable: x => x.tempBuffersSize(),
                            defaultSortOrder: "desc"
                        }),
                        new textColumn<driveUsageDetails>(grid, x => this.sizeFormatter(x.tempBuffersSize() + x.size()), "Total", "16%", {
                            sortable: x => x.tempBuffersSize() + x.size(),
                            defaultSortOrder: "desc"
                        })
                    ] 
                } else {
                    return [
                        new legendColumn<driveUsageDetails>(grid, x =>  this.colorClassProvider(x.database()), "", "26px"),
                        new hyperlinkColumn<driveUsageDetails>(grid, x => x.database(), x => this.getStorageReportUrl(x.database()), "Database", "60%", {
                            extraClass: d => isDisabled(d.database()) ? "disabled" : "",
                            sortable: "string",
                            customComparator: generalUtils.sortAlphaNumeric
                        }),
                        new textColumn<driveUsageDetails>(grid, x => this.sizeFormatter(x.size()), "Data", "30%", {
                            sortable: x => x.size(),
                            defaultSortOrder: "desc"
                        }) 
                    ]
                }
            });
            
            gridInitialization.dispose();
        });

        this.mountPointLabel = ko.pureComputed(() => {
            let mountPoint = this.mountPoint();
            const mountPointLabel = this.volumeLabel();
            if (mountPointLabel) {
                mountPoint += ` (${mountPointLabel})`;
            }

            return mountPoint;
        });
    }
    
    private getStorageReportUrl(database: string) {
        return database === "<System>" ? appUrl.forSystemStorageReport() : appUrl.forStatusStorageReport(database);
    }
    
    update(dto: Raven.Server.Dashboard.MountPointUsage) {
        this.mountPoint(dto.MountPoint);
        this.volumeLabel(dto.VolumeLabel);
        this.totalCapacity(dto.TotalCapacity);
        this.freeSpace(dto.FreeSpace);
        this.isLowSpace(dto.IsLowSpace);
        
        const newDbs = dto.Items.map(x => x.Database);
        const oldDbs = this.items().map(x => x.database());
        
        const removed = _.without(oldDbs, ...newDbs);
        removed.forEach(dbName => {
            const matched = this.items().find(x => x.database() === dbName);
            this.items.remove(matched);
        });
        
        dto.Items.forEach(incomingItem => {
            const matched = this.items().find(x => x.database() === incomingItem.Database);
            if (matched) {
                matched.update(incomingItem);
            } else {
                this.items.push(new driveUsageDetails(incomingItem));
            }
        });

        if (this.gridController()) {
            const selectedItems = this.gridController().getSelectedItems();
            this.gridController().reset(false);
            if (selectedItems && selectedItems.length) {
                // maintain selection after grid update
                this.gridController().setSelectedItems(selectedItems);
            }
        }
    }
}

export = driveUsage;
