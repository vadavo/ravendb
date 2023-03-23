import textColumn = require("widgets/virtualGrid/columns/textColumn");
import indexingSpeedItem = require("models/resources/widgets/indexingSpeedItem");
import clusterDashboard = require("viewmodels/resources/clusterDashboard");
import nodeTagColumn = require("widgets/virtualGrid/columns/nodeTagColumn");
import abstractDatabaseAndNodeAwareTableWidget = require("viewmodels/resources/widgets/abstractDatabaseAndNodeAwareTableWidget");
import virtualColumn = require("widgets/virtualGrid/columns/virtualColumn");
import appUrl = require("common/appUrl");
import perNodeStatItems = require("models/resources/widgets/perNodeStatItems");
import widget = require("viewmodels/resources/widgets/widget");

class databaseIndexingWidget extends abstractDatabaseAndNodeAwareTableWidget<Raven.Server.Dashboard.Cluster.Notifications.DatabaseIndexingSpeedPayload, perNodeStatItems<indexingSpeedItem>, indexingSpeedItem> {

    view = require("views/resources/widgets/databaseIndexingWidget.html");
    
    getType(): Raven.Server.Dashboard.Cluster.ClusterDashboardNotificationType {
        return "DatabaseIndexing";
    }
    
    constructor(controller: clusterDashboard) {
        super(controller);

        for (const node of this.controller.nodes()) {
            const stats = new perNodeStatItems<indexingSpeedItem>(node.tag());
            this.nodeStats.push(stats);
        }
    }

    protected createNoDataItem(nodeTag: string, databaseName: string): indexingSpeedItem {
        return indexingSpeedItem.noData(nodeTag, databaseName);
    }

    protected mapItems(nodeTag: string, data: Raven.Server.Dashboard.Cluster.Notifications.DatabaseIndexingSpeedPayload): indexingSpeedItem[] {
        return data.Items.map(x => new indexingSpeedItem(nodeTag, x));
    }

    protected prepareColumns(): virtualColumn[] {
        const grid = this.gridController();
        return [
            new textColumn<indexingSpeedItem>(grid, x => x.hideDatabaseName ? "" : x.database, "Database", "35%"),
            new nodeTagColumn<indexingSpeedItem>(grid, item => this.prepareUrl(item, "Indexing Performance View")),
            new textColumn<indexingSpeedItem>(grid, x => widget.formatNumber(x.indexedPerSecond), "Indexed/s", "15%", {
                headerTitle: "Indexed items per second"
            }),
            new textColumn<indexingSpeedItem>(grid, x => widget.formatNumber(x.mappedPerSecond), "Mapped/s", "15%", {
                headerTitle: "Mapped items per second"
            }),
            new textColumn<indexingSpeedItem>(grid, x => widget.formatNumber(x.reducedPerSecond), "Reduced/s", "15%", {
                headerTitle: "Reduced mapped entries per second"
            })
        ];
    }

    protected generateLocalLink(database: string): string {
        return appUrl.forIndexPerformance(database);
    }
}


export = databaseIndexingWidget;
