import app = require("durandal/app");
import viewModelBase = require("viewmodels/viewModelBase");
import clusterDashboardWebSocketClient = require("common/clusterDashboardWebSocketClient");
import widget = require("viewmodels/resources/widgets/widget");
import addWidgetModal = require("viewmodels/resources/addWidgetModal");
import clusterTopologyManager = require("common/shell/clusterTopologyManager");
import memoryUsageWidget = require("viewmodels/resources/widgets/memoryUsageWidget");
import cpuUsageWidget = require("viewmodels/resources/widgets/cpuUsageWidget");
import ioStatsWidget = require("viewmodels/resources/widgets/ioStatsWidget");
import licenseWidget = require("viewmodels/resources/widgets/licenseWidget");
import storageWidget = require("viewmodels/resources/widgets/storageWidget");
import clusterNode = require("models/database/cluster/clusterNode");
import websocketBasedWidget = require("viewmodels/resources/widgets/websocketBasedWidget");
import indexingWidget = require("viewmodels/resources/widgets/indexingWidget");
import trafficWidget = require("viewmodels/resources/widgets/trafficWidget");
import welcomeWidget = require("viewmodels/resources/widgets/welcomeWidget");
import databaseIndexingWidget = require("viewmodels/resources/widgets/databaseIndexingWidget");
import databaseStorageWidget = require("viewmodels/resources/widgets/databaseStorageWidget");
import databaseTrafficWidget = require("viewmodels/resources/widgets/databaseTrafficWidget");
import databaseOverviewWidget = require("viewmodels/resources/widgets/databaseOverviewWidget");
import ongoingTasksWidget = require("viewmodels/resources/widgets/ongoingTasksWidget");
import clusterOverviewWidget = require("viewmodels/resources/widgets/clusterOverviewWidget");
import storageKeyProvider = require("common/storage/storageKeyProvider");
import Packery = require("packery");
import Draggabilly = require("draggabilly");

interface savedWidgetsLayout {
    widgets: savedWidget[];
    columns: number;
}

interface savedWidget {
    type: widgetType;
    columnIndex: number;
    fullscreen: boolean;
    config: any;
    state: any;
}

class clusterDashboard extends viewModelBase {

    view = require("views/resources/clusterDashboard.html");

    static localStorageName = storageKeyProvider.storageKeyFor("clusterDashboardLayout");
    
    private static readonly nodeColors = ["#2f9ef3", "#945ab5", "#f06582", "#f0b362", "#7bd85d", "#7069ee", "#d85b9a", "#f38a66", "#edcd51", "#37c4ac"];
    
    private packery: Packery;
    resizeObserver: ResizeObserver;
    initialized = ko.observable<boolean>(false);
    readonly currentServerNodeTag: string;
    private htmlElement: HTMLElement;
    
    widgets = ko.observableArray<widget<any>>([]);
    
    nodes: KnockoutComputed<clusterNode[]>;
    bootstrapped: KnockoutComputed<boolean>;
    
    liveClients = ko.observableArray<clusterDashboardWebSocketClient>([]);
    
    constructor() {
        super();
        
        const topologyManager = clusterTopologyManager.default;

        this.currentServerNodeTag = topologyManager.localNodeTag();
        
        this.nodes = ko.pureComputed(() => {
            const topology = topologyManager.topology();
            if (!topology) {
                return [];
            }
            return topologyManager.topology().nodes();
        });
        
        this.bootstrapped = ko.pureComputed(() => !!this.nodes().length);
    }
    
    private initPackery() {
        this.packery = new Packery(".masonry-grid", { 
            itemSelector: ".cluster-dashboard-item",
            percentPosition: true,
            initialLayout: false,
            columnWidth: ".grid-sizer",
            gutter: ".gutter-sizer",
            transitionDuration: '0',
        });
    }
    
    private afterLayoutInitialized() {
        this.resizeObserver.observe(this.htmlElement);
        
        const throttledLayoutSave = _.debounce(() => {
            this.saveToLocalStorage();
        }, 5_000);

        this.packery.on("layoutComplete", throttledLayoutSave);

        this.initialized(true);
    }

    saveToLocalStorage() {
        const packeryWidth = this.packery.packer.width;
        const layout = this.widgets().map(x => {
            const packeryItem = this.packery.getItem(x.container);
            return {
                left: packeryItem.rect.x / packeryWidth,
                top: packeryItem.rect.y,
                widget: x
            }
        });

        const sortedLayout = layout.sort((a, b) => a.top === b.top ? a.left - b.left : a.top - b.top);

        const columnsCount = this.getNumberOfColumnsInPackeryLayout();

        const widgetsLayout: savedWidgetsLayout = {
            widgets: sortedLayout.map(x => ({
                type: x.widget.getType(),
                fullscreen: x.widget.fullscreen(),
                config: x.widget.getConfiguration(),
                state: x.widget.getState(),
                columnIndex: clusterDashboard.getColumnIndex(x.left, columnsCount)
            })),
            columns: columnsCount
        };

        localStorage.setObject(clusterDashboard.localStorageName, widgetsLayout);
    }
    
    private static getColumnIndex(leftPositionPercentage: number, totalColumns: number): number {
        return Math.round(leftPositionPercentage * totalColumns);
    }
    
    private getNumberOfColumnsInPackeryLayout() {
        const gridSizer = $(".cluster-dashboard-container .grid-sizer").innerWidth();
        return Math.round(this.packery.packer.width / gridSizer);
    }
    
    attached() {
        super.attached();
        
        $("#page-host").css("overflow-y", "scroll");
    }
    
    syncStyles(nodes: clusterNode[]) {
        const styles = document.getElementById("cluster-dashboard-node-styles") as HTMLStyleElement;
        
        const rules = nodes.map((node, idx) => {
            const color = clusterDashboard.nodeColors[idx % clusterDashboard.nodeColors.length];
            return `.node-${node.tag()} { --node-color: ${color}; } `;
        });
        
        styles.innerHTML = rules.join(" ");
    }

    compositionComplete(child?: HTMLElement) {
        super.compositionComplete();
        
        this.htmlElement = child;

        this.syncStyles(this.nodes());
        this.registerDisposable(this.nodes.subscribe(nodes => this.syncStyles(nodes)));
        
        const throttledLayout = _.debounce(() => this.onResized(), 400);
        
        this.resizeObserver = new ResizeObserver(throttledLayout);
        
        if (this.nodes().length) {
            this.initDashboard();
        } else {
            // wait for cluster boostrap
            const awaitClusterInit = this.nodes.subscribe(nodes => {
                if (nodes.length) {
                    this.initialized(false);
                    this.widgets([]);
                    awaitClusterInit.dispose();
                    
                    setTimeout(() => {
                        this.initDashboard();
                    }, 500);
                }
            });
            
            // but in meantime we want to show welcome widget only to avoid empty screen on newly started server
            // (since it isn't bootstrapped by default)
            this.clusterIsNotBootstrapped();
        }
    }
    
    private clusterIsNotBootstrapped() {
        this.initPackery();
        const welcome = this.spawnWidget("Welcome", true);
        this.addWidget(welcome);
        
        welcome.composeTask.done(() => {
            this.onWidgetAdded(welcome);
            this.initialized(true);
        });
    }
    
    private initDashboard(): JQueryPromise<void> {
        this.initPackery();
        
        this.enableLiveView();

        const savedWidgetsLayout: savedWidgetsLayout = localStorage.getObject(clusterDashboard.localStorageName);
        if (savedWidgetsLayout) {
            const currentColumnsCount = this.getNumberOfColumnsInPackeryLayout();
            const sameColumnsCount = savedWidgetsLayout.columns === currentColumnsCount;
            
            const savedLayout = savedWidgetsLayout.widgets;
            const widgets = savedLayout.map(item => this.spawnWidget(item.type, item.fullscreen, item.config, item.state));
            
            widgets.forEach(w => this.addWidget(w));
            
            return $.when(...widgets.map(x => x.composeTask))
                .done(() => {
                    widgets.forEach(w => this.onWidgetAdded(w));
                    
                    if (sameColumnsCount) {
                        // saved and current columns count is the same 
                        // try to restore layout with regard to positions within columns 
                        // and try to respect items orders inside each column 
                        this.packery._resetLayout();
                        const gutterWidth = this.packery.gutter;
                        const itemWidth = this.packery.columnWidth;

                        for (let i = 0; i < savedLayout.length; i++) {
                            const savedItem = savedLayout[i];
                            const widget = widgets[i];
                            const packeryItem = this.packery.getItem(widget.container);
                            packeryItem.rect.x = savedItem.columnIndex * (itemWidth + gutterWidth); 
                        }

                        this.packery.shiftLayout();

                        this.afterLayoutInitialized();
                    } else {
                        // looks like columns count changed - let call fresh layout
                        // but it should maintain item's order
                        // that's all we can do
                        this.packery.layout();
                        this.afterLayoutInitialized();
                    }
                });
        } else {
            this.addWidget(new cpuUsageWidget(this));
            if (clusterTopologyManager.default.hasAnyNodeWithOs("Linux")) {
                this.addWidget(new ioStatsWidget(this));
            }
            this.addWidget(new trafficWidget(this));
            this.addWidget(new databaseTrafficWidget(this));
            this.addWidget(new databaseIndexingWidget(this));
            this.addWidget(new memoryUsageWidget(this));
            this.addWidget(new indexingWidget(this));
            this.addWidget(new storageWidget(this));
            this.addWidget(new licenseWidget(this));
            this.addWidget(new databaseStorageWidget(this));
            this.addWidget(new welcomeWidget(this));
            this.addWidget(new databaseOverviewWidget(this));
            this.addWidget(new ongoingTasksWidget(this));
            this.addWidget(new clusterOverviewWidget(this));
            
            const initialWidgets = this.widgets();
            
            return $.when(...this.widgets().map(x => x.composeTask))
                .done(() => {
                    initialWidgets.forEach(w => this.onWidgetAdded(w));
                    this.afterLayoutInitialized();
                });
        }
    }
    
    private enableLiveView() {
        const nodes = clusterTopologyManager.default.topology().nodes();

        for (const node of nodes) {
            const tag = node.tag();
            const client: clusterDashboardWebSocketClient =
                new clusterDashboardWebSocketClient(tag, d => this.onData(tag, d), () => this.onWebSocketConnected(client), () => this.onWebSocketDisconnected(client));
            this.liveClients.push(client);
        }
    }

    private onWebSocketConnected(ws: clusterDashboardWebSocketClient) {
        for (const widget of this.widgets()) {
            if (widget instanceof websocketBasedWidget) {
                widget.onClientConnected(ws);
            }
        }
    }

    private onWebSocketDisconnected(ws: clusterDashboardWebSocketClient) {
        for (const widget of this.widgets()) {
            if (widget instanceof websocketBasedWidget) {
                widget.onClientDisconnected(ws);
            }
        }
    }

    deactivate() {
        super.deactivate();

        const clients = this.liveClients();
        
        clients.forEach(client => {
            client?.dispose();
        });

        this.liveClients([]);
    }
    
    detached() {
        super.detached();

        if (this.resizeObserver) {
            this.resizeObserver.disconnect();
        }
        
        $("#page-host").css("overflow-y", "");
    }

    deleteWidget(widget: widget<any, any>) {
        this.packery.remove(widget.container);
        this.packery.shiftLayout();
        
        this.widgets.remove(widget);
        
        widget.dispose();
    }
    
    addWidget(widget: widget<any>) {
        this.widgets.push(widget);
    }
    
    private onResized() {
        this.layout();

        for (const widget of this.widgets()) {
            widget.afterComponentResized();
        }
    }

    layout(withDelay = true, mode: "shift" | "full" = "full") {
        const layoutAction = () => {
            mode === "full" ? this.packery.layout() : this.packery.shiftLayout();
        }
        
        if (withDelay) {
            setTimeout(() => {
                layoutAction();
            }, 600);
        } else {
            layoutAction();
        }
    }
    
    getConnectedLiveClients() {
        return this.liveClients().filter(x => x.connectedAt);
    }
    
    getConnectedCurrentNodeLiveClient() {
        return this.getConnectedLiveClients().find(x => x.nodeTag === this.currentServerNodeTag);
    }
    
    onWidgetAdded(widget: widget<any, any>) {
        this.packery.appended([widget.container]);

        const draggie = new Draggabilly(widget.container, {
            handle: ".cluster-dashboard-item-header"
        });
        this.packery.bindDraggabillyEvents(draggie);
    }
 
    private onData(nodeTag: string, msg: Raven.Server.Dashboard.Cluster.WidgetMessage) {
        const targetWidget = this.widgets().find(x => x.id === msg.Id);
        // target widget might be in removal state but 'unwatch' wasn't delivered yet.
        if (targetWidget) {
            if (targetWidget instanceof websocketBasedWidget) {
                targetWidget.onData(nodeTag, msg.Data);
            } else {
                console.error("Tried to deliver message to widget which doesn't support messages. Id = " + msg.Id);
            }
        }
    }
    
    addWidgetModal() {
        const existingWidgetTypes = _.uniq(this.widgets().map(x => x.getType()));
        const addWidgetView = new addWidgetModal(existingWidgetTypes, type => {
            const newWidget = this.spawnWidget(type);
            this.addWidget(newWidget);
            
            newWidget.composeTask.done(() => {
                this.onWidgetAdded(newWidget);
            })
        });
        app.showBootstrapDialog(addWidgetView);
    }
    
    spawnWidget(type: widgetType, fullscreen = false, config: any = undefined, state: any = undefined) {
        let widget: widget<any>;
        
        switch (type) {
            case "Welcome":
                widget = new welcomeWidget(this);
                break;
            case "CpuUsage":
                widget = new cpuUsageWidget(this);
                break;
            case "IoStats":
                widget = new ioStatsWidget(this);
                break;
            case "License":
                widget = new licenseWidget(this);
                break;
            case "MemoryUsage":
                widget = new memoryUsageWidget(this);
                break;
            case "StorageUsage":
                widget = new storageWidget(this);
                break;
            case "Indexing":
                widget = new indexingWidget(this);
                break;
            case "Traffic":
                widget = new trafficWidget(this);
                break;
            case "DatabaseIndexing":
                widget = new databaseIndexingWidget(this);
                break;
            case "DatabaseStorageUsage":
                widget = new databaseStorageWidget(this);
                break;
            case "DatabaseTraffic":
                widget = new databaseTrafficWidget(this);
                break;
            case "DatabaseOverview":
                widget = new databaseOverviewWidget(this);
                break;
            case "OngoingTasks":
                widget = new ongoingTasksWidget(this);
                break;
            case "ClusterOverview":
                widget = new clusterOverviewWidget(this);
                break;
            default:
                throw new Error("Unsupported widget type = " + type);
        }
        
        widget.fullscreen(fullscreen);
        
        if (config) {
            widget.restoreConfiguration(config);
        }
        if (state) {
            widget.restoreState(state);
        }
        
        return widget;
    }
}

export = clusterDashboard;
