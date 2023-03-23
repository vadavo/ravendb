/// <reference path="../../../typings/tsd.d.ts"/>

import accessManager = require("common/shell/accessManager");

class database {
    static readonly type = "database";
    static readonly qualifier = "db";

    name: string;

    disabled = ko.observable<boolean>(false);
    errored = ko.observable<boolean>(false);
    isAdminCurrentTenant = ko.observable<boolean>(false);
    relevant = ko.observable<boolean>(true);
    nodes = ko.observableArray<string>([]);
    hasRevisionsConfiguration = ko.observable<boolean>(false);
    hasExpirationConfiguration = ko.observable<boolean>(false);
    hasRefreshConfiguration = ko.observable<boolean>(false);
    isEncrypted = ko.observable<boolean>(false);
    
    environment = ko.observable<Raven.Client.Documents.Operations.Configuration.StudioConfiguration.StudioEnvironment>();
    environmentClass = database.createEnvironmentColorComputed("label", this.environment);

    databaseAccess = ko.observable<databaseAccessLevel>();
    databaseAccessText = ko.observable<string>();
    databaseAccessColor = ko.observable<string>();
    
    private clusterNodeTag: KnockoutObservable<string>;

    constructor(dbInfo: Raven.Client.ServerWide.Operations.DatabaseInfo, clusterNodeTag: KnockoutObservable<string>) {
        this.clusterNodeTag = clusterNodeTag;

        this.updateUsing(dbInfo);
    }
    
    static createEnvironmentColorComputed(prefix: string, source: KnockoutObservable<Raven.Client.Documents.Operations.Configuration.StudioConfiguration.StudioEnvironment>) {
        return ko.pureComputed(() => {
            const env = source();
            if (env) {
                switch (env) {
                    case "Production":
                        return prefix + "-danger";
                    case "Testing":
                        return prefix + "-success";
                    case "Development":
                        return prefix + "-info";
                }
            }

            return null;
        });
    }

    updateUsing(incomingCopy: Raven.Client.ServerWide.Operations.DatabaseInfo) {
        this.isEncrypted(incomingCopy.IsEncrypted);
        this.hasRevisionsConfiguration(incomingCopy.HasRevisionsConfiguration);
        this.hasExpirationConfiguration(incomingCopy.HasExpirationConfiguration);
        this.hasRefreshConfiguration(incomingCopy.HasRefreshConfiguration);
        this.isAdminCurrentTenant(incomingCopy.IsAdmin);
        this.name = incomingCopy.Name;
        this.disabled(incomingCopy.Disabled);
        this.environment(incomingCopy.Environment !== "None" ? incomingCopy.Environment : null);
        if (incomingCopy.LoadError) {
            this.errored(true);
        }
        
        if (incomingCopy.NodesTopology) {
            const nodeTag = this.clusterNodeTag();
            
            const nodes: string[] = [];
            incomingCopy.NodesTopology.Members.forEach(x => nodes.push(x.NodeTag));
            incomingCopy.NodesTopology.Promotables.forEach(x => nodes.push(x.NodeTag));
            incomingCopy.NodesTopology.Rehabs.forEach(x => nodes.push(x.NodeTag));

            this.relevant(_.includes(nodes, nodeTag));
            this.nodes(nodes);
        }

        const dbAccessLevel = accessManager.default.getEffectiveDatabaseAccessLevel(incomingCopy.Name);
        this.databaseAccess(dbAccessLevel);
        this.databaseAccessText(accessManager.default.getAccessLevelText(dbAccessLevel));
        this.databaseAccessColor(accessManager.default.getAccessColor(dbAccessLevel));
    }

    static getNameFromUrl(url: string) {
        const index = url.indexOf("databases/");
        return (index > 0) ? url.substring(index + 10) : "";
    }

    //TODO: remove those props?
    get fullTypeName() {
        return "Database";
    }

    get qualifier() {
        return database.qualifier;
    }

    get urlPrefix() {
        return "databases";
    }

    get type() {
        return database.type;
    }
}

export = database;
