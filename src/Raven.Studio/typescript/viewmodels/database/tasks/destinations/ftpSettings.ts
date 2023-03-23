﻿import backupSettings = require("models/database/tasks/periodicBackup/backupSettings");
import jsonUtil = require("common/jsonUtil");
import fileImporter = require("common/fileImporter");
import genUtils = require("common/generalUtils");
import popoverUtils = require("common/popoverUtils");
import tasksCommonContent = require("models/database/tasks/tasksCommonContent");

class ftpSettings extends backupSettings {
    view = require("views/database/tasks/destinations/ftpSettings.html");
    
    url = ko.observable<string>();
    port = ko.observable<number>();
    userName = ko.observable<string>();
    password = ko.observable<string>();
    certificateAsBase64 = ko.observable<string>();
    certificateFileName = ko.observable<string>();

    isLoadingFile = ko.observable<boolean>();
    isFtps = ko.pureComputed(() => {
        if (!this.url())
            return false;

        return this.url().toLowerCase().startsWith("ftps://");
    });

    targetOperation: string;

    constructor(dto: Raven.Client.Documents.Operations.Backups.FtpSettings, targetOperation: string) {
        super(dto, "FTP");

        this.url(dto.Url || "");
        this.port(dto.Port);
        this.userName(dto.UserName);
        this.password(dto.Password);
        this.certificateAsBase64(dto.CertificateAsBase64);
        this.certificateFileName(dto.CertificateFileName);

        if (this.certificateAsBase64() && !this.certificateFileName()) {
            // the configuration was updated using the client api
            this.certificateFileName("certificate.cer");
        }

        this.targetOperation = targetOperation;

        this.initValidation();

        this.dirtyFlag = new ko.DirtyFlag([
            this.enabled,
            this.url,
            this.port, 
            this.userName,
            this.password,
            this.certificateAsBase64,
            this.configurationScriptDirtyFlag().isDirty
        ], false, jsonUtil.newLineNormalizingHashFunction);
    }

    compositionComplete() {
        popoverUtils.longWithHover($(".ftp-host-info"),
            {
                content: tasksCommonContent.ftpHostInfo
            });
    }

    initValidation() {
        this.url.extend({
            required: {
                onlyIf: () => this.enabled()
            },
            validation: [
                {
                    validator: (url: string) => {
                        if (!url)
                            return false;

                        const urlLower = url.toLowerCase();
                        if (urlLower.includes("://") && !urlLower.startsWith("ftp://") && !urlLower.startsWith("ftps://")) {
                            return false;
                        }

                        return true;
                    },
                    message: "Url must start with ftp:// or ftps://"
                }
            ]
        });

        this.port.extend({
            validation: [
                {
                    validator: (port: number) => {
                        if (!this.enabled())
                            return true;

                        if (!port)
                            return true;

                        return port >= 1 && port <= 65535;
                    },
                    message: "Port number range: 1-65535"
                }
            ]
        });

        this.userName.extend({
            required: {
                onlyIf: () => this.enabled()
            }
        });

        this.password.extend({
            required: {
                onlyIf: () => this.enabled()
            }
        });

        this.certificateFileName.extend({
            required: {
                onlyIf: () => this.enabled() && this.isFtps()
            }
        });

        this.localConfigValidationGroup = ko.validatedObservable({
            url: this.url,
            port: this.port,
            userName: this.userName,
            password: this.password,
            certificateFileName: this.certificateFileName
        });
    }

    fileSelected(fileInput: HTMLInputElement) {
        this.isLoadingFile(true);
        
        fileImporter.readAsArrayBuffer(fileInput, (data, filename) => {
            this.certificateFileName(filename);

            let binary = "";
            const bytes = new Uint8Array(data);
            for (let i = 0; i < bytes.byteLength; i++) {
                binary += String.fromCharCode(bytes[i]);
            }
            const result = window.btoa(binary);
            this.certificateAsBase64(result);
        })
            .always(() => this.isLoadingFile(false));
    }

    toDto(): Raven.Client.Documents.Operations.Backups.FtpSettings {
        const dto = super.toDto() as Raven.Client.Documents.Operations.Backups.FtpSettings;
        dto.Url = this.url();
        dto.Port = this.port();
        dto.UserName = this.userName();
        dto.Password = this.password();
        dto.CertificateAsBase64 = this.isFtps() ? this.certificateAsBase64() : null;
        dto.CertificateFileName = this.isFtps() ? this.certificateFileName() : null;

        return genUtils.trimProperties(dto, ["Url", "UserName"]);
    }

    static empty(targetOperation: string): ftpSettings {
        return new ftpSettings({
            Disabled: true,
            Url: null,
            Port: null,
            UserName: null,
            Password: null,
            CertificateAsBase64: null,
            CertificateFileName: null,
            GetBackupConfigurationScript: null
        }, targetOperation);
    }
}

export = ftpSettings;
