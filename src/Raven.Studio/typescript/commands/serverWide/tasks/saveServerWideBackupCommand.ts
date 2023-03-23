import commandBase = require("commands/commandBase");
import endpoints = require("endpoints");

class saveServerWideBackupCommand extends commandBase {
    constructor(private configuration: Raven.Client.ServerWide.Operations.Configuration.ServerWideBackupConfiguration) {
        super();
    } 
    
    execute(): JQueryPromise<Raven.Client.ServerWide.Operations.Configuration.PutServerWideBackupConfigurationResponse> {
        const url = endpoints.global.adminServerWide.adminConfigurationServerWideBackup;
        const isNewTask = this.configuration.TaskId === 0;
        
        return this.put<Raven.Client.ServerWide.Operations.Configuration.PutServerWideBackupConfigurationResponse>(url, JSON.stringify(this.configuration))
            .done(() => {
                const taskTypeText = isNewTask ? "created" : "updated";
                this.reportSuccess(`The server-wide backup task was ${taskTypeText} successfully`);
            })
            .fail(response => this.reportError(`Failed to save the server-wide backup task: ${this.configuration.Name}`, response.responseText, response.statusText));
    }
}

export = saveServerWideBackupCommand; 

