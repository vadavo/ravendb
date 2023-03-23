﻿import intermediateMenuItem = require("common/shell/menu/intermediateMenuItem");
import leafMenuItem = require("common/shell/menu/leafMenuItem");

export = getStatsMenuItem;

function getStatsMenuItem(appUrls: computedAppUrls) {
    const statsItems: menuItem[] = [
        new leafMenuItem({
            route: 'databases/status',
            moduleId: require('viewmodels/database/status/statistics'),
            title: 'Stats',
            nav: true,
            css: 'icon-stats',
            dynamicHash: appUrls.status
        }),
        new leafMenuItem({
            route: 'databases/status/ioStats',
            moduleId: require('viewmodels/database/status/ioStats'),
            title: 'IO Stats',
            tooltip: "Displays IO metrics status",
            nav: true,
            css: 'icon-io-test',
            dynamicHash: appUrls.ioStats
        }),
        new leafMenuItem({
            route: 'databases/status/storage/report',
            moduleId: require('viewmodels/database/status/storageReport'),
            title: 'Storage Report',
            tooltip: "Storage Report",
            nav: true,
            css: 'icon-storage',
            dynamicHash: appUrls.statusStorageReport
        }),
        new leafMenuItem({
            route: "virtual", // here we only redirect to global section with proper db set in url
            moduleId: () => { /* empty */},
            title: 'Running Queries',
            nav: true,
            css: 'icon-stats-running-queries',
            dynamicHash: appUrls.runningQueries
        }),
        new leafMenuItem({
            route: 'databases/status/ongoingTasksStats',
            moduleId: require('viewmodels/database/status/ongoingTasksStats'),
            title: 'Ongoing Tasks Stats',
            nav: true,
            css: 'icon-replication-stats',
            dynamicHash: appUrls.ongoingTasksStats
        }),
        new leafMenuItem({
            route: 'databases/status/debug*details',
            moduleId: null,
            title: 'Debug',
            nav: false,
            css: 'icon-debug'
        })
    ];

    return new intermediateMenuItem("Stats", statsItems, "icon-stats-menu");
}
