﻿using System.Collections.Generic;
using FastTests;
using Raven.Client.Documents;
using Raven.Client.Documents.Indexes;
using Raven.Client.Documents.Operations.Indexes;
using Xunit;
using Xunit.Abstractions;

namespace SlowTests.Issues;

public class RavenDB_18936 : RavenTestBase
{
    public RavenDB_18936(ITestOutputHelper output) : base(output)
    {
    }
    
    [Fact]
    public void CanCreateDynamicFieldsInsideMapJsIndex()
    {
        using var store = GetDocumentStore();

        var index = new CreateFieldInsideMapJavaScript();
        index.Execute(store);

        using (var s = store.OpenSession())
        {
            s.Store(new Item {Attributes = new[]
            {
                new Attribute("T1", 10.99m), 
                new Attribute("T2", 12.99m), 
                new Attribute("T3", 13.99m)
            }});

            s.Store(new Item {Attributes = new[]
            {
                new Attribute("T1", 11.99m)
            }});

            s.SaveChanges();
        }

        using (var s = store.OpenSession())
        {
            var items = s.Advanced.DocumentQuery<Item, CreateFieldInsideMapJavaScript>()
                .WaitForNonStaleResults()
                .OrderByDescending("T1")
                .ToList();
            Assert.Equal(2, items.Count);
            AssertTerm(store, index.IndexName, "T1", new[] {"10.99", "11.99"});
            AssertTerm(store, index.IndexName, "T2", new[] {"12.99"});
            AssertTerm(store, index.IndexName, "T3", new[] {"13.99"});
        }
    }

    private void AssertTerm(DocumentStore store, string index, string fieldName, string[] termsThatShouldBeStored, bool isSpatial = false)
    {
        var terms = store
            .Maintenance
            .Send(new GetTermsOperation(index, fieldName, null, int.MaxValue));
        if (isSpatial == false)
            Assert.Equal(termsThatShouldBeStored.Length, terms.Length);
        foreach (var term in termsThatShouldBeStored)
        {
            Assert.Contains(term, terms);
        }
    }

    [Fact]
    public void CanCreateDynamicFieldFromArray()
    {
        using var store = GetDocumentStore();
        {
            using var session = store.OpenSession();
            session.Store(new Item()
            {
                Id = "Maciej"
            });
            session.SaveChanges();
        }

        var index = new CreateFieldInsideArrayJavaScript();
        index.Execute(store);
        Indexes.WaitForIndexing(store);
        
        AssertTerm(store, index.IndexName, "name", new []{"john"});
        
        WaitForUserToContinueTheTest(store);
    }
    
    [Fact]
    public void SpatialFieldsCreatedInsideMap()
    {
        using var store = GetDocumentStore();
        {
            using var session = store.OpenSession();
            session.Store(new GeoDocument("Maciej", 
                new []
                {
                    new Coordinate(10,10), 
                    new (20,20)
                }));
            session.SaveChanges();
        }
        var index = new CreateSpatialInsideMap();
        index.Execute(store);
        Indexes.WaitForIndexing(store);

        WaitForUserToContinueTheTest(store);
        AssertTerm(store, index.IndexName, "Loc", new []
        {
            Spatial4n.Core.Util.GeohashUtils.EncodeLatLon(10,10,9),
            Spatial4n.Core.Util.GeohashUtils.EncodeLatLon(20,20,9)
        }
            , isSpatial: true);
    }
    
    private class CreateSpatialInsideMap : AbstractJavaScriptIndexCreationTask
    {
        public CreateSpatialInsideMap()
        {
            Maps = new HashSet<string>
            {
                @"map('GeoDocuments', function (p){
    return {
        NameInIndex: p.Id,
        Loc: p.Coordinates.map(x => createSpatialField(x.Lat, x.Lon))
    };
})"
            };
        }
    }
    
    private class CreateFieldInsideArrayJavaScript : AbstractJavaScriptIndexCreationTask
    {
        public CreateFieldInsideArrayJavaScript()
        {
            Maps = new HashSet<string>
            {
                @"map('Items', function (p) {
return {
_: [ createField('name', 'john', { indexing: 'Exact', storage: false, termVector: null }) ]
};
})",
            };
        }
    }
    
    private class CreateFieldInsideMapJavaScript : AbstractJavaScriptIndexCreationTask
    {
        public CreateFieldInsideMapJavaScript()
        {
            Maps = new HashSet<string>
            {
                @"map('Items', function (p) {
return {
_: p.Attributes.map(x => createField(x.Name, x.Value, 
                                                        { 
                                                            indexing: 'Exact',
                                                            storage: true,
                                                            termVector: null
                                                        })               
)};})",
            };
        }
    }
    
    private class Item
    {
        public string Id { get; set; }
        public Attribute[] Attributes { get; set; }
    }

    private class Attribute
    {
        public Attribute(string name, decimal value)
        {
            Name = name;
            Value = value;
        }

        public string Name { get; set; }
        public decimal Value { get; set; }
    }
    
    private record GeoDocument(string Id, Coordinate[] Coordinates);

    private record Coordinate(double Lat, double Lon);
}
