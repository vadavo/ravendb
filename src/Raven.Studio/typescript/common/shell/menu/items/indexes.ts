﻿import intermediateMenuItem = require("common/shell/menu/intermediateMenuItem");
import leafMenuItem = require("common/shell/menu/leafMenuItem");
import footer = require("common/shell/footer");
export = getIndexesMenuItem;

function getIndexesMenuItem(appUrls: computedAppUrls) {
    const indexesItems = [
        new leafMenuItem({
            route: 'databases/query/index(/:indexNameOrRecentQueryIndex)',
            moduleId: require('viewmodels/database/query/query'),
            title: 'Query',
            nav: true,
            css: 'icon-indexes-query',
            dynamicHash: appUrls.query('')
        }),
        new leafMenuItem({
            title: "List of Indexes",
            nav: true,
            route: "databases/indexes",
            moduleId: require("viewmodels/database/indexes/indexes"),
            css: 'icon-list-of-indexes',
            dynamicHash: appUrls.indexes
        }),
        new leafMenuItem({
            route: 'databases/indexes/performance',
            moduleId: require('viewmodels/database/indexes/indexPerformance'),
            title: 'Indexing Performance',
            tooltip: "Shows details about indexing peformance",
            nav: true,
            css: 'icon-indexing-performance',
            dynamicHash: appUrls.indexPerformance
        }),
        new leafMenuItem({
            route: 'databases/indexes/visualizer',
            moduleId: require('viewmodels/database/indexes/visualizer/visualizer'),
            title: 'Map-Reduce Visualizer',
            nav: true,
            css: 'icon-map-reduce-visualizer',
            dynamicHash: appUrls.visualizer
        }),
        new leafMenuItem({
            route: 'databases/indexes/indexErrors',
            moduleId: require('viewmodels/database/indexes/indexErrors'),
            title: 'Index Errors',
            nav: true,
            css: 'icon-index-errors',
            dynamicHash: appUrls.indexErrors,
            badgeData: ko.pureComputed(() => { return footer.default.stats() ? footer.default.stats().countOfIndexingErrors() : null; })
        }),
        new leafMenuItem({
            title: 'Edit Index',
            route: 'databases/indexes/edit(/:indexName)',
            moduleId: require('viewmodels/database/indexes/editIndex'),
            css: 'icon-edit',
            nav: false,
            itemRouteToHighlight: 'databases/indexes'
        }),
        new leafMenuItem({
            title: 'Terms',
            route: 'databases/indexes/terms/(:indexName)',
            moduleId: require('viewmodels/database/indexes/indexTerms'),
            css: 'icon-terms',
            nav: false
        })
    ];

    return new intermediateMenuItem("Indexes", indexesItems, 'icon-indexing', {
        dynamicHash: appUrls.indexes
    });
}
