# VITA Application Framework  

VITA is a full-featured .NET [ORM](http://en.wikipedia.org/wiki/Object-relational_mapping)

## Quick Highlights
* **Entities are defined as .NET interfaces** - minimum coding required. Just _'string Name {get;set;}'_ for a property - compact and clear. 
* **Entities are self-tracking** - they maintain original and modified property values, system automatically tracks modified entities to submit in *SaveChanges* call. 
* **Database model is automatically created/updated from entity model**. Existing data is preserved. Database schema follows c# code. 
* **Database keys and indexes** are defined through entity attributes in c# - you can define most of the database artifacts in c# code.
* **Automatically generated CRUD SQLs**. 
* **Support for one-to-many and many-to-many relationships.** Foreign keys are automatically inferred from c# entity references. Properties which are lists of related entities are automatically "filled-up" on first read. 
* **Full LINQ support** for database querying. And not only SELECTs - you can use *LINQ expressions to execute INSERT, UPDATE and DELETE* statements.
* **Database views** generated from LINQ queries, including materialized views with indexes.
* **DB-first approach is fully supported** - VITA provides a code generator that produces the complete source code for the solution from the existing database schema. 
* **Batched operations** - multiple database update/delete/insert commands are combined in a single multi-line command executed in one round-trip to the database.  
* **Component Packaging Technology** - with VITA we have a way to pack a set of entities/tables with the surrounding code into a self-contained component - entity module. Entity modules can cover specific areas of functionality, can be independently developed, tested and distributed, just like Windows Control libraries. The application is then assembled from independent modules, each covering specific part of functionality. 
* **Full support for identity columns and auto-generated GUIDs**
* **Compiled LINQ Query Cache** - dynamic LINQ queries are compiled into SQL on the first use, and later served from the query cache, avoiding the overhead of SQL translation. 
* **Computed properties**.
* **Entities support INotifyPropertyChanged interface** - readily usable in data binding and MVVM solutions.
* **Web API stack integration**. Fully integrated with Web API technology stack, provides numerous benefits for easy creation of fast and reliable RESTful data services. 
* **Built-in efficient logging and diagnostics infrastructure** with logging to file or database. Easy to setup other logging targets. 


### System Requirements
* .NET Standard 2.0, Visual Studio 2019; .NET Core 3.1 for test and sample projects 
* MS SQL Server 2012+; MySql, PostgreSQL, Oracle, SQLite
