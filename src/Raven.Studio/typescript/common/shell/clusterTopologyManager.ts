﻿/// <reference path="../../../typings/tsd.d.ts"/>

import clusterTopology = require("models/database/cluster/clusterTopology");
import getClusterTopologyCommand = require("commands/database/cluster/getClusterTopologyCommand");
import changesContext = require("common/changesContext");
import licenseModel = require("models/auth/licenseModel");
import getClusterNodeInfoCommand = require("commands/database/cluster/getClusterNodeInfoCommand");
import OSType = Raven.Client.ServerWide.Operations.OSType;

class clusterTopologyManager {

    static default = new clusterTopologyManager();

    topology = ko.observable<clusterTopology>();
    
    nodeInfo = ko.observable<Raven.Client.ServerWide.Commands.NodeInfo>();

    localNodeTag: KnockoutComputed<string>;
    localNodeUrl: KnockoutComputed<string>;
    
    currentTerm: KnockoutComputed<number>;
    votingInProgress: KnockoutComputed<boolean>;
    nodesCount: KnockoutComputed<number>;
    
    throttledLicenseUpdate = _.throttle(() => licenseModel.fetchLicenseStatus(), 5000);
    
    init(): JQueryPromise<void> {
        return $.when<any>(this.fetchTopology(), this.fetchNodeInfo());
    }
    
    private fetchNodeInfo() {
        return new getClusterNodeInfoCommand()
            .execute()
            .done(nodeInfo => {
                this.nodeInfo(nodeInfo);
            });
    }

    private fetchTopology() {
        return new getClusterTopologyCommand()
            .execute()
            .done(topology => {
                this.topology(topology);
            });
    }

    constructor() {
        this.initObservables();
    }

    setupGlobalNotifications() {
        const serverWideClient = changesContext.default.serverNotifications();

        serverWideClient.watchClusterTopologyChanges(e => this.onTopologyUpdated(e));
    }

    private onTopologyUpdated(e: Raven.Server.NotificationCenter.Notifications.Server.ClusterTopologyChanged) {
        this.topology().updateWith(e);
        this.throttledLicenseUpdate();
    }
    
    public getClusterNodeByTag(nodeTag: string) {
        const topology = this.topology();
        if (!topology) {
            return null;
        }
        
        return topology.nodes().find(x => x.tag() === nodeTag);
    }
    
    public hasAnyNodeWithOs(os: OSType) {
        const topology = this.topology();
        if (topology) {
            const nodes = topology.nodes();
            if (nodes && nodes.length) {
                return nodes.some(node => node.osInfo() && node.osInfo().Type === os);
            }
        }
        
        return false;
    }

    private initObservables() {
        this.currentTerm = ko.pureComputed(() => {
            const topology = this.topology();
            return topology ? topology.currentTerm() : null;
        });
        
        this.localNodeTag = ko.pureComputed(() => {
            const topology = this.topology();
            return topology ? topology.nodeTag() : null;
        });

        this.localNodeUrl = ko.pureComputed(() => {
            const localNode = _.find(this.topology().nodes(), x => x.tag() === this.localNodeTag());
            return localNode ? localNode.serverUrl() : null;
        });

        this.nodesCount = ko.pureComputed(() => {
            const topology = this.topology();
            return topology ? topology.nodes().length : 0;
        });

        this.votingInProgress = ko.pureComputed(() => {
            const topology = this.topology();
            if (!topology) {
                return false;
            }

            return topology.currentState() === "Candidate";
        });
    }
}

export = clusterTopologyManager;
