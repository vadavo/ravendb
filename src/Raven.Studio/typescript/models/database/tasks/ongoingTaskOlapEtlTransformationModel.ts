﻿/// <reference path="../../../../typings/tsd.d.ts"/>
import collectionsTracker = require("common/helpers/database/collectionsTracker");
import jsonUtil = require("common/jsonUtil");
import validateNameCommand = require("commands/resources/validateNameCommand");
import generalUtils = require("common/generalUtils");

class ongoingTaskOlapEtlTransformationModel {

    static readonly applyToAllCollectionsText = "Apply to All Collections";
    
    name = ko.observable<string>();
    script = ko.observable<string>();

    inputCollection = ko.observable<string>();
    transformScriptCollections = ko.observableArray<string>([]);
    
    canAddCollection: KnockoutComputed<boolean>;
    applyScriptForAllCollections = ko.observable<boolean>(false);

    isNew = ko.observable<boolean>(true);
    resetScript = ko.observable<boolean>(false);
    
    validationGroup: KnockoutValidationGroup;
    dirtyFlag: () => DirtyFlag;
    
    constructor(dto: Raven.Client.Documents.Operations.ETL.Transformation, isNew: boolean, resetScript: boolean) {
        this.update(dto, isNew, resetScript);
        
        this.initObservables();
        this.initValidation();
    }

    static isApplyToAll(colectionName: string){
        return colectionName === ongoingTaskOlapEtlTransformationModel.applyToAllCollectionsText;
    }

    initObservables() {
        this.canAddCollection = ko.pureComputed(() => {
            const collectionToAdd = this.inputCollection();
            return collectionToAdd && !this.transformScriptCollections().find(x => x === collectionToAdd);
        });
        
        this.dirtyFlag = new ko.DirtyFlag([
            this.name,
            this.script,
            this.resetScript,
            this.applyScriptForAllCollections,
            this.transformScriptCollections
       ], false, jsonUtil.newLineNormalizingHashFunction);
    }
   
    static empty(name?: string): ongoingTaskOlapEtlTransformationModel {
        return new ongoingTaskOlapEtlTransformationModel(
            {
                ApplyToAllDocuments: false, 
                Collections: [],
                Disabled: false,
                Name: name || "",
                Script: ""
            }, true, false);
    }

    toDto(): Raven.Client.Documents.Operations.ETL.Transformation {
        return {
            ApplyToAllDocuments: this.applyScriptForAllCollections(),
            Collections: this.applyScriptForAllCollections() ? null : this.transformScriptCollections(),
            Disabled: false,
            Name: this.name(),
            Script: this.script()
        }
    }

    private initValidation() {
        this.script.extend({
            required: true,
            aceValidation: true
        });

        const checkScriptName = (val: string,
                                 params: any,
                                 callback: (currentValue: string, errorMessageOrValidationResult: string | boolean) => void) => {
            new validateNameCommand('Script', val)
                .execute()
                .done((result) => {
                    if (result.IsValid) {
                        callback(this.name(), true);
                    } else {
                        callback(this.name(), result.ErrorMessage);
                    }
                })
        };
        
        this.name.extend({
            required: true,
            validation: [
                {
                    async: true,
                    validator: generalUtils.debounceAndFunnel(checkScriptName)
                }
            ]
        });

        this.transformScriptCollections.extend({
            validation: [
                {
                    validator: () => this.applyScriptForAllCollections() || this.transformScriptCollections().length > 0,
                    message: "At least one collection is required"
                }
            ]
        });

        this.validationGroup = ko.validatedObservable({
            name: this.name,
            script: this.script,
            transformScriptCollections: this.transformScriptCollections,
        });
    }

    addCollection() {
        this.addWithBlink(this.inputCollection());
    }
    
    removeCollection(collection: string) {
        this.transformScriptCollections.remove(collection);
        this.applyScriptForAllCollections(false);
    }

    addWithBlink(collectionName: string) {
        if (ongoingTaskOlapEtlTransformationModel.isApplyToAll(collectionName)) {
            this.applyScriptForAllCollections(true);
            this.transformScriptCollections([ongoingTaskOlapEtlTransformationModel.applyToAllCollectionsText]);
        } else {
            this.applyScriptForAllCollections(false);
            this.transformScriptCollections.unshift(collectionName);
            this.transformScriptCollections.remove(ongoingTaskOlapEtlTransformationModel.applyToAllCollectionsText);
        }

        this.inputCollection("");

        // blink on newly created item
        $(".collection-list li").first().addClass("blink-style");
    }
    
    update(dto: Raven.Client.Documents.Operations.ETL.Transformation, isNew: boolean, resetScript: boolean) {
        this.name(dto.Name);
        this.script(dto.Script);
        
        this.transformScriptCollections(dto.Collections || []);
        this.applyScriptForAllCollections(dto.ApplyToAllDocuments);

        if (this.applyScriptForAllCollections()) {
            this.transformScriptCollections([ongoingTaskOlapEtlTransformationModel.applyToAllCollectionsText]);
        }
        
        this.isNew(isNew);
        this.resetScript(resetScript);
    }

    getCollectionEntry(collectionName: string) {
        return collectionsTracker.default.getCollectionColorIndex(collectionName);
    }

    hasUpdates(oldItem: this) {
        const hashFunction = jsonUtil.newLineNormalizingHashFunctionWithIgnoredFields(["__moduleId__", "validationGroup"]);
        return hashFunction(this) !== hashFunction(oldItem);
    }
}

export = ongoingTaskOlapEtlTransformationModel;
