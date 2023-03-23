import commandBase = require("commands/commandBase");
import endpoints = require("endpoints");

class compactDatabaseCommand extends commandBase {

    constructor(private databaseName: string, private compactDocuments: boolean, private indexesToCompact: Array<string>) {
        super();
    }

    execute(): JQueryPromise<operationIdDto> {
        const payload: Raven.Client.ServerWide.CompactSettings = {
            DatabaseName: this.databaseName,
            Documents: this.compactDocuments,
            Indexes: this.indexesToCompact,
            SkipOptimizeIndexes: false
        };

        const url = endpoints.global.adminDatabases.adminCompact;
        
        return this.post<operationIdDto>(url, JSON.stringify(payload))
            .fail((response: JQueryXHR) => this.reportError("Failed to compact database", response.responseText, response.statusText));
    }


} 

export = compactDatabaseCommand;
