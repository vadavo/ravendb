import clusterDashboard = require("viewmodels/resources/clusterDashboard");
import nodeTagColumn = require("widgets/virtualGrid/columns/nodeTagColumn");
import abstractDatabaseAndNodeAwareTableWidget = require("viewmodels/resources/widgets/abstractDatabaseAndNodeAwareTableWidget");
import virtualColumn = require("widgets/virtualGrid/columns/virtualColumn");
import textColumn = require("widgets/virtualGrid/columns/textColumn");
import appUrl = require("common/appUrl");
import trafficWatchItem = require("models/resources/widgets/trafficWatchItem");
import generalUtils = require("common/generalUtils");
import perNodeStatItems = require("models/resources/widgets/perNodeStatItems");
import widget = require("viewmodels/resources/widgets/widget");

class databaseTrafficWidget extends abstractDatabaseAndNodeAwareTableWidget<Raven.Server.Dashboard.Cluster.Notifications.DatabaseTrafficWatchPayload, 
    perNodeStatItems<trafficWatchItem>, trafficWatchItem> {

    view = require("views/resources/widgets/databaseTrafficWidget.html");
    
    getType(): Raven.Server.Dashboard.Cluster.ClusterDashboardNotificationType {
        return "DatabaseTraffic";
    }

    constructor(controller: clusterDashboard) {
        super(controller);

        for (const node of this.controller.nodes()) {
            const stats = new perNodeStatItems<trafficWatchItem>(node.tag());
            this.nodeStats.push(stats);
        }
    }

    protected createNoDataItem(nodeTag: string, databaseName: string): trafficWatchItem {
        return trafficWatchItem.noData(nodeTag, databaseName);
    }

    protected mapItems(nodeTag: string, data: Raven.Server.Dashboard.Cluster.Notifications.DatabaseTrafficWatchPayload): trafficWatchItem[] {
        return data.Items.map(x => new trafficWatchItem(nodeTag, x));
    }

    protected prepareColumns(): virtualColumn[] {
        const grid = this.gridController();
        return [
            new textColumn<trafficWatchItem>(grid, x => x.hideDatabaseName ? "" : x.database, "Database", "30%"),
            new nodeTagColumn<trafficWatchItem>(grid, item => this.prepareUrl(item, "Traffic Watch View")),
            new textColumn<trafficWatchItem>(grid, x => widget.formatNumber(x.requestsPerSecond), "Requests/s", "12%", {
                headerTitle: "Requests made to node per second"
            }),
            new textColumn<trafficWatchItem>(grid, x => widget.formatNumber(x.writesPerSecond), "Writes/s", "12%", {
                headerTitle: "Items written by node per second"
            }),
            new textColumn<trafficWatchItem>(grid, x => x.noData ? "-" : generalUtils.formatBytesToSize(x.dataWritesPerSecond), "Data written/s", "12%", {
                headerTitle: "Bytes written by node per second"
            }),
            new textColumn<trafficWatchItem>(grid, x => x.noData ? "-" : Math.round(x.averageDuration).toLocaleString() + " ms", "Avg Req Time", "12%", {
                headerTitle: "Average request time"
            }),
        ];
    }

    protected generateLocalLink(database: string): string {
        return appUrl.forTrafficWatch(database);
    }
}

export = databaseTrafficWidget;
