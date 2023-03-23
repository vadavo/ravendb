import database = require("models/resources/database");
import collection = require("models/database/documents/collection");

class collectionsStats  {

    numberOfConflicts: number;
    numberOfDocuments = ko.observable<number>();
    collections: collection[];

    
    constructor(statsDto: Raven.Client.Documents.Operations.CollectionStatistics, ownerDatabase: database) {
        this.numberOfDocuments(statsDto.CountOfDocuments);
        this.numberOfConflicts = statsDto.CountOfConflicts;

        this.collections = [];

        for (const key in statsDto.Collections) {
            // eslint-disable-next-line no-prototype-builtins
            if (!statsDto.Collections.hasOwnProperty(key))
                continue;
            this.collections.push(new collection(key, ownerDatabase, statsDto.Collections[key]));
        }
    }

    getCollectionCount(collectionName: string): number {
        const matchedCollection = this.collections.find(x => x.name === collectionName);
        return matchedCollection ? matchedCollection.documentCount() : 0;
    }
   
}

export = collectionsStats;
