/// <reference path="../../../../typings/tsd.d.ts"/>
import d3 = require('d3');

class storagePieChart {

    private width: number;
    private svg: d3.Selection<void>;
    private colorClassScale: d3.scale.Ordinal<string, string>;
    private containerSelector: string;
    private highlightTable: (dbName: string) => void;
    
    constructor(containerSelector: string, highlightTable: (dbName: string) => void) {
        this.containerSelector = containerSelector;
        this.highlightTable = highlightTable;
        const container = d3.select(containerSelector);

        const $container = $(containerSelector);

        this.width = Math.min($container.innerHeight(), $container.innerWidth());

        this.svg = container
            .append("svg")
            .attr("width", this.width)
            .attr("height", this.width);
        
        this.svg
            .append("g")
            .attr("class", "pie")
            .attr("transform", "translate(" + (this.width / 2) + ", " + (this.width / 2) + ")");

        this.colorClassScale = d3.scale.ordinal<string>()
            .range(_.range(1, 11).map(x => "color-" + x));
        
        this.initHighlightEvents($container);
    }
    
    private initHighlightEvents(container: JQuery) {
        $(container).on("mouseenter", "path", event => {
            const dbName = $(event.target).attr("data-db-name");
            this.highlightTable(dbName);
        });
        
        $(container).on("mouseleave", "path", () => {
            this.highlightTable(null);
        });
    }
    
    highlightDatabase(dbName: string, container: JQuery) {
        if (dbName) {
            
            // here we iterate over matching elements and compare values to avoid issues with
            // CSS.escape method like: escape on number, etc. see: RavenDB-12600
            $("[data-db-name]", container).each((idx, node) => {
                const $node = $(node);
                if ($node.attr("data-db-name") === dbName) {
                    $node.addClass("active");
                }
            });
        } else {
            $("path", container).removeClass("active");
        }
    }
    
    onResize() {
        const container = d3.select(this.containerSelector);

        const $container = $(this.containerSelector);

        this.width = Math.min($container.innerHeight(), $container.innerWidth());

        this.svg = container
            .select("svg")
            .attr("width", this.width)
            .attr("height", this.width);
        
        this.svg
            .select("g.pie")
            .attr("transform", "translate(" + (this.width / 2) + ", " + (this.width / 2) + ")")
            .selectAll(".arc")
            .remove();
    }

    onData(data: Array<{ Database: string, Size: number }>, withTween = false) {
        const group = this.svg.select(".pie");

        const arc = d3.svg.arc<Raven.Server.Dashboard.DatabaseDiskUsage>()
            .innerRadius(16)
            .outerRadius(this.width / 2 - 10);

        const pie = d3.layout.pie<{ Database: string, Size: number }>()
            .value(x => x.Size)
            .sort(null);
        
        const arcs = group.selectAll(".arc")
            .data(pie(data), x => x.data.Database);
        
        arcs.exit().remove();
        
        const enteringArcs = arcs
            .enter()
            .append("g")
            .attr("class", "arc");
       
        const paths = enteringArcs.append("path");
        
        paths
            .attr("data-db-name", d => d.data.Database)
            .attr("d", d => arc(d as any))
            .attr("class", d => this.getDatabaseColorForPie(d.data.Database))
            .each(function (d) { this._current = d; });
        
        paths
            .append("title")
            .html(d => d.data.Database === '<System>' ? '&ltSystem&gt' : d.data.Database);

        if (withTween) {
            const arcTween = function (a: d3.layout.pie.Arc<{ Database: string, Size: number }>) {
                const i = d3.interpolate(this._current , a as any);
                this._current = i(0);
                return (t: number) => arc(i(t) as any);
            };
            
            arcs.select("path")
                .transition()
                .attrTween("d", arcTween)
                .attr("class", d => this.getDatabaseColorForPie(d.data.Database));
        } else {
            arcs.select("path")
                .attr("d", d => arc(d as any))
                .attr("class", d => this.getDatabaseColorForPie(d.data.Database));
        }
    }
    
    private getDatabaseColorForPie(dbName: string): string {
        return dbName === '<System>' ? "color-11" : this.colorClassScale(dbName);        
    }

    getColorClassProvider() {
        return (dbName: string) => this.getDatabaseColorForPie(dbName);
    }
}

export = storagePieChart;
