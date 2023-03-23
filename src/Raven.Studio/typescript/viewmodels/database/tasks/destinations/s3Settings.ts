﻿import amazonSettings = require("models/database/tasks/periodicBackup/amazonSettings");
import jsonUtil = require("common/jsonUtil");
import genUtils = require("common/generalUtils");
import popoverUtils = require("common/popoverUtils");
import tasksCommonContent = require("models/database/tasks/tasksCommonContent");

class s3Settings extends amazonSettings {
    
    view = require("views/database/tasks/destinations/s3Settings.html");
    
    bucketName = ko.observable<string>();
    useCustomS3Host = ko.observable<boolean>();
    customServerUrl = ko.observable<string>();
    forcePathStyle = ko.observable<boolean>();
    accessKeyPropertyName: KnockoutComputed<string>;
    secretKeyPropertyName: KnockoutComputed<string>;

    targetOperation: string;

    constructor(dto: Raven.Client.Documents.Operations.Backups.S3Settings, allowedRegions: Array<string>, targetOperation: string) {
        super(dto, "S3", allowedRegions);

        this.bucketName(dto.BucketName);
        this.customServerUrl(dto.CustomServerUrl);
        this.forcePathStyle(dto.ForcePathStyle);
        this.useCustomS3Host(!!dto.CustomServerUrl);
        this.targetOperation = targetOperation;
        
        this.initValidation();

        this.dirtyFlag = new ko.DirtyFlag([
            this.enabled,
            this.bucketName,
            this.awsAccessKey,
            this.awsSecretKey,
            this.awsRegionName,
            this.remoteFolderName,
            this.selectedAwsRegion,
            this.customServerUrl,
            this.forcePathStyle,
            this.useCustomS3Host,
            
            this.configurationScriptDirtyFlag().isDirty
        ], false, jsonUtil.newLineNormalizingHashFunction);

        this.useCustomS3Host.subscribe(() => {
            if (this.testConnectionResult()) {
                this.testConnectionResult(null);
            }
        });

        this.accessKeyPropertyName = ko.pureComputed(() => s3Settings.getAccessKeyPropertyName(this.useCustomS3Host(), this.customServerUrl()));
        this.secretKeyPropertyName = ko.pureComputed(() => s3Settings.getSecretKeyPropertyName(this.useCustomS3Host(), this.customServerUrl()));
    }

    static getAccessKeyPropertyName(useCustomS3Host: boolean, customServerUrl: string) {
        return s3Settings.isBackBlaze(useCustomS3Host, customServerUrl) ? "Application Key ID" : "Access key";
    }

    static getSecretKeyPropertyName(useCustomS3Host: boolean, customServerUrl: string) {
        return s3Settings.isBackBlaze(useCustomS3Host, customServerUrl) ? "Application Key" : "Secret key";
    }

    private static isBackBlaze(useCustomS3Host: boolean, customServerUrl: string) {
        return useCustomS3Host && customServerUrl && customServerUrl.toLowerCase().endsWith(".backblazeb2.com");
    }
    
    compositionComplete(view: Element, container: HTMLElement) {
        popoverUtils.longWithHover($(".bucket-info", container),
            {
                content: tasksCommonContent.textForPopover("Bucket", this.targetOperation)
            });
    }
    
    initValidation() {
        super.initAmazonValidation();
        
        /* Bucket name must :
            - be at least 3 and no more than 63 characters long.
            - be a series of one or more labels. 
                Adjacent labels are separated by a single period (.). 
                Bucket names can contain lowercase letters, numbers, and hyphens. 
                Each label must start and end with a lowercase letter or a number.
            - not be formatted as an IP address (e.g., 192.168.5.4).
        */

        const ipRegExp = /^[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}$/;
        const letterOrNumberRegex = /^[a-z0-9]+$/;
        const regExp = /^[a-z0-9.-]+$/;
        this.bucketName.extend({
            validation: [
                {
                    validator: (bucketName: string) => bucketName && bucketName.length >= 3 && bucketName.length <= 63,
                    message: "Bucket name should be between 3 and 63 characters long"
                },
                {
                    validator: (bucketName: string) => bucketName && regExp.test(bucketName),
                    message: "Allowed characters are lowercase characters, numbers, periods, and dashes"
                },
                {
                    validator: (bucketName: string) => bucketName && letterOrNumberRegex.test(bucketName[0]),
                    message: "Bucket name should start with a number or letter"
                },
                {
                    validator: (bucketName: string) => bucketName && letterOrNumberRegex.test(bucketName[bucketName.length - 1]),
                    message: "Bucket name should end with a number or letter"
                },
                {
                    validator: (bucketName: string) => bucketName && !bucketName.includes(".."),
                    message: "Bucket name cannot contain consecutive periods"
                },
                {
                    validator: (bucketName: string) => bucketName && !bucketName.includes(".-") && !bucketName.includes("-."),
                    message: "Bucket names cannot contain dashes next to periods (e.g. \" -.\" and/or \".-\")"
                },
                {
                    validator: (bucketName: string) => !ipRegExp.test(bucketName),
                    message: "Bucket name must not be formatted as an IP address (e.g., 192.168.5.4)"
                }
            ]
        });
        
        this.customServerUrl.extend({
            required: {
                onlyIf: () => this.useCustomS3Host()
            },
            validUrl: {
                onlyIf: () => this.useCustomS3Host()
            }
        });

        this.localConfigValidationGroup = ko.validatedObservable({
            awsAccessKey: this.awsAccessKey,
            awsSecretKey: this.awsSecretKey,
            awsRegionName: this.awsRegionName,
            bucketName: this.bucketName,
            customServerUrl: this.customServerUrl
        });
    }
    
    isRegionRequired() {
        const isRegionRequired = this.useCustomS3Host ? !this.hasConfigurationScript() && !this.useCustomS3Host() :
                                                        !this.hasConfigurationScript();
        return super.isRegionRequired() && isRegionRequired;
    }

    toDto(): Raven.Client.Documents.Operations.Backups.S3Settings {
        const dto = super.toDto() as Raven.Client.Documents.Operations.Backups.S3Settings;
        
        dto.BucketName = this.bucketName();
        dto.CustomServerUrl = !this.hasConfigurationScript() && this.useCustomS3Host() ? this.customServerUrl() : undefined;
        dto.ForcePathStyle = !this.hasConfigurationScript() && this.useCustomS3Host() ? this.forcePathStyle() : false;
        
        return genUtils.trimProperties(dto, ["CustomServerUrl", "RemoteFolderName", "AwsRegionName", "AwsAccessKey"]);
    }

    static empty(allowedRegions: Array<string>, targetOperation: string): s3Settings {
        return new s3Settings({
            Disabled: true,
            AwsAccessKey: null,
            AwsRegionName: null,
            AwsSecretKey: null,
            AwsSessionToken: null,
            BucketName: null,
            RemoteFolderName: null,
            GetBackupConfigurationScript: null,
            ForcePathStyle: false,
            CustomServerUrl: null,
        }, allowedRegions, targetOperation);
    }
}

export = s3Settings;
