import viewModelBase = require("viewmodels/viewModelBase");
import getSystemStorageReportCommand = require("commands/resources/getSystemStorageReportCommand");
import generalUtils = require("common/generalUtils");
import storageReportItem = require("models/database/status/storageReportItem");
import d3 = require("d3");

type positionAndSizes = {
    dx: number,
    dy: number,
    x: number,
    y: number
}

class storageReport extends viewModelBase {

    view = require("views/manage/storageReport.html");

    static readonly animationLength = 200;

    private rawData: detailedSystemStorageReportItemDto;

    private currentPath: KnockoutComputed<Array<storageReportItem>>;

    private x: d3.scale.Linear<number, number>;
    private y: d3.scale.Linear<number, number>;
    private root: storageReportItem;
    private node = ko.observable<storageReportItem>();
    private treemap: d3.layout.Treemap<any>;
    private svg: d3.Selection<any>;
    private tooltip: d3.Selection<any>;

    private w: number;
    private h: number;

    private transitioning = false;

    showPagesColumn: KnockoutObservable<boolean>;
    showEntriesColumn: KnockoutObservable<boolean>;
    showTempFiles: KnockoutObservable<boolean>;

    constructor() {
        super();
        this.bindToCurrentInstance("onClick");
    }

    activate(args: any) {
        super.activate(args);

        this.initObservables();

        return new getSystemStorageReportCommand()
            .execute()
            .done(result => {
                this.rawData = result;
            });
    }

    compositionComplete() {
        super.compositionComplete();
        this.processData();
        this.initGraph();
        this.draw(undefined);
    }

    private initObservables() {
        this.currentPath = ko.pureComputed(() => {
            const node = this.node();

            const items: storageReportItem[] = [];

            let currentItem = node;
            while (currentItem) {
                items.unshift(currentItem);
                currentItem = currentItem.parent;
            }

            return items;
        });

        this.showEntriesColumn = ko.pureComputed(() => {
            const node = this.node();
            return !!node.internalChildren.find(x => x.type === "table" || x.type === "tree");
        });

        this.showPagesColumn = ko.pureComputed(() => {
            const node = this.node();
            return !!node.internalChildren.find(x => x.type === "tree");
        });
        
        this.showTempFiles = ko.pureComputed(() => {
            return this.node() == this.root;
        })
    }

    private processData() {
        const data = this.rawData;
        this.root = this.mapReport(data);
        this.sortBySize(this.root);

        this.node(this.root);
    }

    private sortBySize(node: storageReportItem) {
        if (node.internalChildren && node.internalChildren.length) {
            node.internalChildren.forEach(x => this.sortBySize(x));

            node.internalChildren.sort((a, b) => d3.descending(a.size, b.size));
        }
    }

    private mapReport(reportItem: detailedSystemStorageReportItemDto): storageReportItem {
        const dataFile = this.mapDataFile(reportItem.Report);
        const journals = this.mapJournals(reportItem.Report);
        const tempFiles = this.mapTempFiles(reportItem.Report);

        return new storageReportItem(reportItem.Environment,
            reportItem.Type.toLowerCase(),
            true,
            dataFile.size + journals.size + tempFiles.size,
            [dataFile, journals, tempFiles]);
    }

    private mapDataFile(report: Voron.Debugging.DetailedStorageReport): storageReportItem {
        const dataFile = report.DataFile;

        const d = new storageReportItem("Datafile", "data", false, dataFile.AllocatedSpaceInBytes);
        const tables = this.mapTables(report.Tables);
        const trees = this.mapTrees(report.Trees, "Trees");
        const freeSpace = new storageReportItem("Free", "free", false, report.DataFile.FreeSpaceInBytes, []);
        const preallocatedBuffers = this.mapPreAllocatedBuffers(report.PreAllocatedBuffers);

        d.internalChildren = [tables, trees, freeSpace, preallocatedBuffers];
        
        return d;
    }

    private mapPreAllocatedBuffers(buffersReport: Voron.Debugging.PreAllocatedBuffersReport): storageReportItem {
        const allocationTree = this.mapTree(buffersReport.AllocationTree);
        const buffersSpace = new storageReportItem("Pre Allocated Buffers Space", "reserved", false, buffersReport.PreAllocatedBuffersSpaceInBytes);
        buffersSpace.pageCount = buffersReport.NumberOfPreAllocatedPages;

        const preAllocatedBuffers = new storageReportItem("Pre Allocated Buffers", "reserved", false, buffersReport.AllocatedSpaceInBytes, [allocationTree, buffersSpace]);
        preAllocatedBuffers.customSizeProvider = (header: boolean) => {
            const allocatedSizeFormatted = generalUtils.formatBytesToSize(buffersReport.AllocatedSpaceInBytes);
            if (header) {
                return allocatedSizeFormatted;
            }
            const originalSizeFormatted = generalUtils.formatBytesToSize(buffersReport.OriginallyAllocatedSpaceInBytes);
            return `<span title="${allocatedSizeFormatted} available out of ${originalSizeFormatted} reserved">${allocatedSizeFormatted} (out of ${originalSizeFormatted})</span>`;
        };
        return preAllocatedBuffers;
    }

    private mapTables(tables: Voron.Data.Tables.TableReport[]): storageReportItem {
        const mappedTables = tables.map(x => this.mapTable(x));

        return new storageReportItem("Tables", "tables", false, mappedTables.reduce((p, c) => p + c.size, 0), mappedTables);
    }

    private mapTable(table: Voron.Data.Tables.TableReport): storageReportItem {
        const structure = this.mapTrees(table.Structure, "Structure");

        const data = new storageReportItem("Table Data", "table_data", false, table.DataSizeInBytes, []);
        const indexes = this.mapTrees(table.Indexes, "Indexes");

        const preallocatedBuffers = this.mapPreAllocatedBuffers(table.PreAllocatedBuffers);

        const totalSize = table.AllocatedSpaceInBytes;

        const tableItem = new storageReportItem(table.Name, "table", true, totalSize, [
            structure,
            data,
            indexes,
            preallocatedBuffers
        ]);

        tableItem.numberOfEntries = table.NumberOfEntries;

        return tableItem;
    }

    private mapTrees(trees: Voron.Debugging.TreeReport[], name: string): storageReportItem {
        return new storageReportItem(name, name.toLowerCase(), false, trees.reduce((p, c) => p + c.AllocatedSpaceInBytes, 0), trees.map(x => this.mapTree(x)));
    }

    private mapTree(tree: Voron.Debugging.TreeReport): storageReportItem {
        const children = (tree.Streams && tree.Streams.Streams) ? tree.Streams.Streams.map(x => this.mapStream(x)) : [];
        const item = new storageReportItem(tree.Name, "tree", true, tree.AllocatedSpaceInBytes, children);
        item.pageCount = tree.PageCount;
        item.numberOfEntries = tree.NumberOfEntries;
        return item;
    }

    private mapStream(stream: Voron.Debugging.StreamDetails): storageReportItem {
        const item = new storageReportItem(stream.Name, "stream", false, stream.AllocatedSpaceInBytes, []);

        item.customSizeProvider = (header: boolean) => {
            const allocatedSizeFormatted = generalUtils.formatBytesToSize(stream.AllocatedSpaceInBytes);
            if (header) {
                return allocatedSizeFormatted;
            }
            const length = generalUtils.formatBytesToSize(stream.Length);
            return `<span title="stream length: ${length} / total allocation: ${allocatedSizeFormatted}">${length} / ${allocatedSizeFormatted}</span>`;
        }

        return item;
    }

    private mapJournals(report: Voron.Debugging.DetailedStorageReport): storageReportItem {
        const journals = report.Journals.Journals;

        const mappedJournals = journals.map(journal => 
            new storageReportItem(
                "Journal #" + journal.Number,
                "journal",
                false,
                journal.AllocatedSpaceInBytes,
                []
            ));

        return new storageReportItem("Journals", "journals", false, mappedJournals.reduce((p, c) => p + c.size, 0), mappedJournals);
    }
    
    private mapTempFiles(report: Voron.Debugging.DetailedStorageReport): storageReportItem {
        const tempFiles = report.TempBuffers;

        const mappedTemps = tempFiles.map(temp => {
            const item = new storageReportItem(
                temp.Name,
                "temp",
                false,
                temp.AllocatedSpaceInBytes,
                []
            );
            
            item.recyclableJournal = temp.Type === "RecyclableJournal";
            
            return item;
        });

        return new storageReportItem("Temporary Files", "tempFiles", false, mappedTemps.reduce((p, c) => p + c.size, 0), mappedTemps);
    }

    private initGraph() {
        this.detectContainerSize();
        this.x = d3.scale.linear().range([0, this.w]);
        this.y = d3.scale.linear().range([0, this.h]);

        this.svg = d3.select("#storage-report-container .chart")
            .append("svg:svg")
            .attr("width", this.w)
            .attr("height", this.h)
            .attr("transform", "translate(.5,.5)");

        this.addHashing();
    }

    private detectContainerSize() {
        const $chartNode = $("#storage-report-container .chart");
        this.w = $chartNode.width();
        this.h = $chartNode.height();
    }

    private addHashing() {
        const defs = this.svg.append('defs');
        const g = defs.append("pattern")
            .attr('id', 'hash')
            .attr('patternUnits', 'userSpaceOnUse')
            .attr('width', '10')
            .attr('height', '10')
            .append("g").style("fill", "none")
            .style("stroke", "grey")
            .style("stroke-width", 1);
        g.append("path").attr("d", "M0,0 l10,10");
        g.append("path").attr("d", "M10,0 l-10,10");
    }

    private getChildren(node: storageReportItem, depth: number) {
        return depth === 0 ? node.internalChildren : [];
    }

    private draw(goingIn: boolean) {
        const levelDown = goingIn === true;
        const levelUp = goingIn === false;

        this.treemap = d3.layout.treemap<any>()
            .children((n, depth) => this.getChildren(n, depth))
            .value(d => d.size)
            .size([this.w, this.h]);

        this.tooltip = d3.select(".chart-tooltip");

        const oldLocation: positionAndSizes = {
            dx: this.node().dx,
            dy: this.node().dy,
            x: this.node().x,
            y: this.node().y
        };

        const nodes = this.treemap.nodes(this.node())
            .filter(n => !n.children);

        if (levelDown) {
            this.animateZoomIn(nodes, oldLocation);
        } else if (levelUp) {
            this.animateZoomOut(nodes);
        } else {
            // initial state
            this.svg.select(".treemap")
                .remove();
            const container = this.svg.append("g")
                .classed("treemap", true);
            this.drawNewTreeMap(nodes, container);
        }
    }

    private animateZoomIn(nodes: storageReportItem[], oldLocation: positionAndSizes) {
        this.transitioning = true;

        const oldContainer = this.svg.select(".treemap");

        const newGroup = this.svg.append("g")
            .classed("treemap", true);

        const scaleX = this.w / oldLocation.dx;
        const scaleY = this.h / oldLocation.dy;
        const transX = -oldLocation.x * scaleX;
        const transY = -oldLocation.y * scaleY;

        oldContainer
            .selectAll("text")
            .transition()
            .duration(storageReport.animationLength / 4)
            .style('opacity', 0);

        oldContainer
            .transition()
            .duration(storageReport.animationLength)
            .attr("transform", "translate(" + transX + "," + transY + ")scale(" + scaleX + "," + scaleY + ")")
            .each("end", () => {
                const newCells = this.drawNewTreeMap(nodes, newGroup);
                newCells
                    .style('opacity', 0)
                    .transition()
                    .duration(storageReport.animationLength)
                    .style('opacity', 1)
                    .each("end", () => {
                        oldContainer.remove();
                        this.transitioning = false;
                    });
            });
    }

    private animateZoomOut(nodes: storageReportItem[]) {
        this.transitioning = true;

        const oldContainer = this.svg.select(".treemap");

        const newGroup = this.svg.append("g")
            .classed("treemap", true);

        const newCells = this.drawNewTreeMap(nodes, newGroup);

        newCells
            .style('opacity', 0)
            .transition()
            .duration(storageReport.animationLength)
            .style('opacity', 1)
            .each("end", () => {
                oldContainer.remove();
                this.transitioning = false;
            });
    }

    private drawNewTreeMap(nodes: storageReportItem[], container: d3.Selection<any>) {
        // eslint-disable-next-line @typescript-eslint/no-this-alias
        const self = this;
        const showTypeOffset = 7;
        const showTypePredicate = (d: storageReportItem) => d.showType && d.dy > 22 && d.dx > 20;

        const cell = container.selectAll("g.cell-no-such") // we always select non-existing nodes to draw from scratch - we don't update elements here
            .data(nodes)
            .enter().append("svg:g")
            .attr("class", d => "cell " + d.type)
            .attr("transform", d => "translate(" + d.x + "," + d.y + ")")
            .on("click", d => this.onClick(d, true))
            .on("mouseover", d => this.onMouseOver(d))
            .on("mouseout", () => this.onMouseOut())
            .on("mousemove", () => this.onMouseMove());

        const rectangles = cell.append("svg:rect")
            .attr("width", d => Math.max(0, d.dx - 1))
            .attr("height", d => Math.max(0, d.dy - 1));

        rectangles
            .filter(x => x.hasChildren())
            .style('cursor', 'pointer');

        cell.append("svg:text")
            .filter(d => d.dx > 20 && d.dy > 8)
            .attr("x", d => d.dx / 2)
            .attr("y", d => showTypePredicate(d) ? d.dy / 2 - showTypeOffset : d.dy / 2)
            .attr("dy", ".35em")
            .attr("text-anchor", "middle")
            .text(d => d.name)
            .each(function (d) {
                self.wrap(this, d.dx);
            });

        cell.filter(d => showTypePredicate(d))
            .append("svg:text")
            .attr("x", d => d.dx / 2)
            .attr("y", d => showTypePredicate(d) ? d.dy / 2 + showTypeOffset : d.dy / 2)
            .attr("dy", ".35em")
            .attr("text-anchor", "middle")
            .text(d => _.upperFirst(d.type))
            .each(function (d) {
                self.wrap(this, d.dx);
            });

        return cell;
    }

    wrap($self: any, width: number) {
        const self = d3.select($self);
        let textLength = (self.node() as any).getComputedTextLength();
        let text = self.text();
        while (textLength > (width - 6) && text.length > 0) {
            text = text.slice(0, -1);
            self.text(text + '...');
            textLength = (self.node() as any).getComputedTextLength();
        }
    } 

    onClick(d: storageReportItem, goingIn: boolean) {
        if (this.transitioning || this.node() === d) {
            return;
        }

        if (!d.internalChildren || !d.internalChildren.length) {
            return;
        }

        this.node(d);
        this.draw(goingIn);

        this.updateTooltips();
        
        if (d3.event) {
            (d3.event as any).stopPropagation();
        }
    }
    
    private updateTooltips() {
        $('#storage-report [data-toggle="tooltip"]').tooltip();
    }

    private onMouseMove() {
        // eslint-disable-next-line prefer-const
        let [x, y] = d3.mouse(this.svg.node());

        const tooltipWidth = $(".chart-tooltip").width() + 20;

        x = Math.min(x, Math.max(this.w - tooltipWidth, 0));

        this.tooltip
            .style("left", (x + 10) + "px")
            .style("top", (y + 10) + "px");
    }

    private onMouseOver(d: storageReportItem) {
        this.tooltip.transition()
            .duration(200)
            .style("opacity", 1);
        let html = "<span class='name'>Name: " + d.name + "</span>";
        if (d.showType) {
            html += "<span>Type: <strong>" + _.upperFirst(d.type) + "</strong></span>";
        }
        if (this.shouldDisplayNumberOfEntries(d)) {
            html += "<span>Entries: <strong>" + d.numberOfEntries.toLocaleString() + "</strong></span>";
        }
        html += "<span class='size'>Size: <strong>" + generalUtils.formatBytesToSize(d.size) + "</strong></span>";

        this.tooltip.html(html);
        this.onMouseMove();
    }

    private shouldDisplayNumberOfEntries(d: storageReportItem) {
        return d.type === "tree" || d.type === "table";
    }

    private onMouseOut() {
        this.tooltip.transition()
            .duration(500)
            .style("opacity", 0);
    }
}

export = storageReport;
