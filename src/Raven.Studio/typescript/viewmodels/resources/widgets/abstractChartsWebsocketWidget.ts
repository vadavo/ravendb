import websocketBasedWidget = require("viewmodels/resources/widgets/websocketBasedWidget");
import historyAwareNodeStats = require("models/resources/widgets/historyAwareNodeStats");
import clusterDashboard = require("viewmodels/resources/clusterDashboard");
import clusterDashboardWebSocketClient = require("common/clusterDashboardWebSocketClient");
import moment = require("moment");
import { lineChart } from "models/resources/clusterDashboard/lineChart";

abstract class abstractChartsWebsocketWidget<
    TPayload extends Raven.Server.Dashboard.Cluster.AbstractClusterDashboardNotification, 
    TNodeStats extends historyAwareNodeStats<TPayload>,
    TConfig = unknown, 
    TState = unknown
    > extends websocketBasedWidget<TPayload, TConfig, TState> {
    
    protected readonly throttledShowHistory: (date: Date) => void;

    protected charts: lineChart[] = [];

    nodeStats = ko.observableArray<TNodeStats>([]);

    protected constructor(controller: clusterDashboard) {
        super(controller);

        this.throttledShowHistory = _.throttle((d: Date) => this.showNodesHistory(d), 100);
    }

    compositionComplete() {
        super.compositionComplete();
        this.enableSyncUpdates();

        for (const ws of this.controller.getConnectedLiveClients()) {
            this.onClientConnected(ws);
        }

        this.charts = this.initCharts();
    }

    protected static tooltipContent(date: Date | null) {
        if (date) {
            const dateFormatted = moment(date).format(lineChart.timeFormat);
            return `<div class="tooltip-inner"><div class="tooltip-li">Time: <div class="value">${dateFormatted}</div></div></div>`;
        } else {
            return null;
        }
    }

    protected abstract initCharts(): lineChart[];

    onMouseMove(date: Date | null) {
        this.charts.forEach(chart => chart.highlightTime(date));

        this.throttledShowHistory(date);
    }

    protected showNodesHistory(date: Date | null) {
        this.nodeStats().forEach(nodeStats => {
            nodeStats.showItemAtDate(date);
        });
    }

    protected withStats(nodeTag: string, action: (stats: TNodeStats) => void) {
        const stats = this.nodeStats().find(x => x.tag === nodeTag);
        if (stats) {
            action(stats);
        }
    }

    onClientConnected(ws: clusterDashboardWebSocketClient) {
        super.onClientConnected(ws);

        this.withStats(ws.nodeTag, x => x.onConnectionStatusChanged(true, ws.connectedAt));
    }

    onClientDisconnected(ws: clusterDashboardWebSocketClient) {
        super.onClientDisconnected(ws);
        
        // flush pending changes - as we redraw anyway 
        this.forceSyncUpdate();
        
        const now = new Date();

        this.withStats(ws.nodeTag, x => x.onConnectionStatusChanged(false));
        this.charts.forEach(chart => chart.recordNoData(now, abstractChartsWebsocketWidget.chartKey(ws.nodeTag)));
    }

    protected afterSyncUpdate() {
        this.charts.forEach(chart => chart.draw());
    }

    afterComponentResized() {
        super.afterComponentResized();
        this.charts.forEach(chart => chart.onResize());
        this.charts.forEach(chart => chart.draw());
    }
    
    private static chartKey(nodeTag: string) {
        return "node-" + nodeTag;
    }

    onData(nodeTag: string, data: TPayload) {
        this.scheduleSyncUpdate(() => this.withStats(nodeTag, x => x.onData(data)));

        const date = moment.utc(data.Date).toDate();

        this.scheduleSyncUpdate(() => {
            this.charts.forEach(chart => {
                const extractedData = this.extractDataForChart(chart, data);
                if (typeof extractedData !== "undefined") {
                    chart.onData(date, [{
                        key: abstractChartsWebsocketWidget.chartKey(nodeTag),
                        value: extractedData
                    }]);
                }
            })
        });
    }

    /**
     * extract data for given chart
     * return undefined if data is not defined at given point
     * @param chart target chart
     * @param data source data
     * @protected
     */
    protected abstract extractDataForChart(chart: lineChart, data: TPayload): number | undefined;
}

export = abstractChartsWebsocketWidget;
