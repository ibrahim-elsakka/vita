﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.ComponentModel;
using System.Reflection;
using System.Collections;

using Vita.Entities.Logging;
using Vita.Entities.Services;
using Vita.Entities.Services.Implementations;
using Vita.Entities.Utilities;
using Vita.Data;
using Vita.Data.Linq;

namespace Vita.Entities.Model.Construction {

  using Binary = Vita.Entities.Binary;

  public partial class EntityModelBuilder {
    public readonly EntityModel Model;
    public readonly IActivationLog Log;


    EntityModelCustomizationService _customization; 
    EntityApp _app;
    List<EntityModelAttributeBase> _allAttributes;

    //Just for testing, to detect possible bugs when processing depends on attribute application order, 
    // we randomize the order. The process should run correctly no matter what the original order of discovered attrs is. 
    public static bool RandomizeAttributesOrder = false;

    public EntityModelBuilder(EntityApp app) {
      _app = app;
      Log = _app.ActivationLog;
      _customization = (EntityModelCustomizationService) _app.GetService<IEntityModelCustomizationService>();
      Model = _app.Model = new EntityModel(_app);
#if DEBUG
      RandomizeAttributesOrder = true;
#endif 
    }

    public void BuildModel() {
      Log.Info("  Building entity model...", _app.AppName);
      _customization.Closed = true; 

      CollectEntitiesAndViews();
      //Model customization
      ProcessReplacedEntities();
      BuildEntityMembers();
      ProcessAddedMembers();
      ProcessAddedIndexes(); 
      if(Failed())
        return; 

      VerifyPrimaryKeys();

      Model.ModelState = EntityModelState.Draft; 
      _app.AppEvents.OnModelConstructing(this);

      CollectInitializeAttributes(); 
      ApplyAttributesProcessRefsLists();
      ExpandEntityKeyMembers();
      SetKeyNames(); 

      Model.ModelState = EntityModelState.Constructed;
      _app.AppEvents.OnModelConstructing(this);

      ValidateKeys();

      CompleteEntitiesSetup();
      BuildDefaultInitialValues();
      BuildEntityClasses();
      ComputeTopologicalIndexes();
      CollectEnumTypes();

      if(Failed())
        return;
      Model.ModelState = EntityModelState.Completed;
      _app.AppEvents.OnModelConstructing(this);
      //fire event
      _app.AppEvents.OnInitializing(EntityAppInitStep.Initialized);
      Log.Info("Entity model built successfully.");
    }//method

    /// <summary>
    /// Checks activation log messages and throws exception if there were any errors during application initialization.
    /// </summary>
    public void CheckErrors() {
      Log.CheckErrors("Entity Model build failed.");
    }

    internal void LogError(string message, params object[] args) {
      Log.Error(message, args);
    }

    private bool Failed() {
      return Log.HasErrors;
    }

    //Collects registered entities - creates EntityInfo objects for each entity and adds them to Model's Entities set. 
    private void CollectEntitiesAndViews() {
      // Collect initial entities
      foreach(var module in _app.Modules) {
        foreach(var entType in module.Entities)
          AddEntity(module, entType);
        foreach(var view in module.Views) {
          if (!ValidateViewDefinition(view)) 
            continue;
          // Create entityInfo for the view
          var entInfo = AddEntity(module, view.EntityType, EntityKind.View);
          entInfo.ViewDefinition = view;
          if (!string.IsNullOrEmpty(view.Name))
            entInfo.TableName = view.Name;
        }//foreach view
      }
    }//method

    private EntityInfo AddEntity(EntityModule module, Type entityType, EntityKind kind = EntityKind.Table) {
      EntityInfo entInfo = Model.GetEntityInfo(entityType);
      if(entInfo != null)
        return entInfo; // tolerate dupes
      var area = _customization.GetNewAreaForEntity(entityType); //might be null
      entInfo = new EntityInfo(module, entityType, kind, area);
      // Do not use inherited attributes for Views
      var allAttrs = entityType.GetAllAttributes(inherit: kind == EntityKind.Table);
      entInfo.Attributes.AddRange(allAttrs);
      Model.RegisterEntity(entInfo); 
      return entInfo;
    }



    private bool ValidateViewDefinition(ViewDefinition view) {
      if(!(view.Query is EntityQuery)) {
        LogError("View definition error ({0}): query must be an entity-based query.", view.Name);
        return false;
      }
      bool ok = true; 
      if (!view.EntityType.IsInterface) {
        LogError("View definition error ({0}), invalid view entity {1} - must be an interface.", view.Name, view.EntityType);
        ok = false; 
      }
      var queryOutType = view.Query.Expression.Type; //.Command.ResultType;
      if (!queryOutType.IsGenericType) {
        LogError("View definition error ({0}): query must return IQueryable<T> generic type.", view.EntityType);
        ok = false;
      }
      if (!ok) return false; 
      var outObjType = queryOutType.GetGenericArguments()[0];
      if (outObjType == view.EntityType)
        return true; 
      // Query output is auto type; check that its properties match properties of view entity
      var entProps = view.EntityType.GetAllProperties();
      foreach(var entProp in entProps) {
        var outProp = outObjType.GetProperty(entProp.Name);
        if (outProp == null) {
          LogError("View definition error ({0}): view property '{1}' not returned by the query.", view.EntityType, entProp.Name);
          ok = false;
          continue; //next prop 
        }
        if (outProp.PropertyType != entProp.PropertyType) {
          LogError("View definition error ({0}): data type for view property '{1}' ({2} ) does not match query output property type ({3}) .",
            view.EntityType, entProp.Name, entProp.PropertyType, outProp.PropertyType);
          ok = false;
        }
      }// foeach entProp
      return ok; 
    }

    // Verify that entities referenced in properties are registered.
    private void VerifyPrimaryKeys() {
      foreach(var entInfo in Model.Entities) {
        if(entInfo.Kind == EntityKind.View)
          continue; 
        var hasPk = entInfo.Attributes.OfType<PrimaryKeyAttribute>().Any();
        if(hasPk)
          continue;
        //check PK on members
        hasPk = entInfo.Members.Any(m => m.Attributes.OfType<PrimaryKeyAttribute>().Any());
        if(!hasPk)
          Log.Error("Entity {0} has no PrimaryKey attribute.", entInfo.EntityType);
      }
      CheckErrors(); 
    }//method


    private void CheckReferencedEntity(Type entityType, string propertyName, EntityInfo owner) {
      EntityInfo entInfo = Model.GetEntityInfo(entityType);
      if (entInfo == null) 
        LogError("Property {0}.{1}: referenced entity type {2} is not registered as an entity.", owner.EntityType.Name, propertyName, entityType.Name);
    }

    private void BuildEntityMembers() {
      foreach (var entInfo in Model.Entities) {
        bool isTable = entInfo.Kind == EntityKind.Table; 
        var props = entInfo.EntityType.GetAllProperties();
        EntityMemberInfo member; 
        foreach (var prop in props) {
          EntityMemberKind kind;
          if (TryGetMemberKind(entInfo, prop, out kind)) {
            member = new EntityMemberInfo(entInfo, kind, prop); //member is added to EntityInfo.Members automatically
            member.Size = member.GetDefaultMemberSize();
            var memberAttrs = prop.GetAllAttributes();
            // if it is table, or prop is declared on THIS entity (not inherited), take all attributes
            if(isTable || prop.DeclaringType == entInfo.EntityType)
              member.Attributes.AddRange(memberAttrs);
            else {
              // for inherited members on views we take only OneToMany and ManyToMany attributes; 
              //  - we want to allow list properties on view entities (see vFictionBook view in Sample book store)
              var listAttrs = memberAttrs.Where(a => a is ManyToManyAttribute || a is OneToManyAttribute).ToList();
              member.Attributes.AddRange(listAttrs); 
            }
          } //else - (kind not found) - do nothing, the TryGetMemberKind should have logged the message
        }
      }//foreach entType
    }

    private bool TryGetMemberKind(EntityInfo entity, PropertyInfo property, out EntityMemberKind kind) {
      return TryGetMemberKind(entity, property.Name, property.PropertyType, out kind);
    }

    private bool TryGetMemberKind(EntityInfo entity, string memberName, Type dataType, out EntityMemberKind kind) {
      kind = EntityMemberKind.Column;
      if (dataType.IsValueType || dataType == typeof(string)) 
        return true; 
      var genType = dataType.IsGenericType ? dataType.GetGenericTypeDefinition() : null;
      if (genType == typeof(Nullable<>)) 
        return true;  
      if (Model.IsEntity(dataType)) {
        kind = EntityMemberKind.EntityRef;
        var target = Model.GetEntityInfo(dataType);
        if (target != null)
          return true;
        LogError("Invalid entity reference, type {2} is not registered as an entity. Entity member: {0}.{1}", entity.Name, memberName, dataType);
        return false;         
      }
      if (genType == typeof(IList<>)) {
        kind = EntityMemberKind.EntityList;
        return true;
      }
      // properly report common mistake
      if (genType == typeof(List<>)) {
        this.LogError("Invalid entity member {0}.{1}. Use IList<T> interface for list members. ", entity.Name, memberName);
        return false; 
      }
      //default: Column
      return true; //Column; there are some specific types that turn into column (Binary for ex)
    }

    private void CollectInitializeAttributes() {
      _allAttributes = new List<EntityModelAttributeBase>();
      foreach(var ent in Model.Entities) {
        var entAttrs = ent.Attributes.SelectModelAttributes();
        entAttrs.Each(a => a.HostEntity = ent);
        _allAttributes.AddRange(entAttrs);
        foreach(var member in ent.Members) {
          var mAttrs = member.Attributes.SelectModelAttributes();
          mAttrs.Each(a => { a.HostMember = member; a.HostEntity = ent; });
          _allAttributes.AddRange(mAttrs);
        }
      }
      if(RandomizeAttributesOrder)
        RandomHelper.RandomizeListOrder(_allAttributes);
      // validate
      foreach(var attr in _allAttributes) {
        if(!attr.Validated)
          attr.Validate(this.Log);
        attr.Validated = true;
      }
      CheckErrors(); 
    }

    internal EntityKeyInfo FindCreateKeyByAlias(EntityInfo entity, string keyAlias) {
      var key = entity.Keys.FirstOrDefault(k => keyAlias.Equals(k.Alias, StringComparison.OrdinalIgnoreCase));
      if(key != null)
        return key;
      // Key does not exist(yet). Let's try to find key attribute with this alias and create the key
      var keyAttrs = entity.GetKeyAttributes();
      var matchAttr = keyAttrs.FirstOrDefault(ka => keyAlias.Equals(ka.Alias, StringComparison.OrdinalIgnoreCase));
      if(matchAttr == null)
        return null;
      // create key
      matchAttr.CreateKey(this.Log);
      return matchAttr.Key; 
    }

    private void ApplyAttributesProcessRefsLists() {
      //group and order by apply order, sort groups
      var keyAttrs = _allAttributes.Where(a => a.ApplyOrder == AttributeApplyOrder.System).ToList();
      foreach(var ka in keyAttrs)
        ka.Apply(this);
      CheckErrors();
      ProcessEntityRefMembers();
      CheckErrors(); 
      ProcessEntityListMembers();
      CheckErrors();
      var otherAttrs = _allAttributes.Where(a => a.ApplyOrder > AttributeApplyOrder.System)
        .OrderBy(a => a.ApplyOrder).ToList();
      foreach(var attr in otherAttrs)
        attr.Apply(this);
      CheckErrors(); 

    }

    private void ValidateKeys() {
      foreach (var entity in Model.Entities) {
        //Clustered indexes
        var ciKeys = entity.Keys.FindAll(k => k.KeyType.IsSet(KeyType.Clustered));
        switch (ciKeys.Count) {
          case 0:   break; //nothing to do
          case 1:
            entity.Flags |= EntityFlags.HasClusteredIndex;
            break;
          default:
            LogError("More than one clustered index specified on entity {0}", entity.FullName);
            break;
        } //switch
        // verify key members
        foreach(var key in entity.Keys) {
          foreach(var km in key.KeyMembers) {
            switch(km.Member.Kind) {
              case EntityMemberKind.EntityList:
              case EntityMemberKind.Transient:
                Log.Error("Invalid key member {0}, entity {1}: must be a property matched to a database column.", 
                  km.Member.MemberName, entity.EntityType);
                break; 
            }
          }
        }
      }//foreach entity
      CheckErrors(); 
    }//method


    //Important - this should be done after processing attributes
    private void CompleteEntitiesSetup() {
      foreach (var ent in Model.Entities) {
        ent.PersistentValuesCount = 0;
        ent.TransientValuesCount = 0;
        var hasUpdatableMembers = false;
        foreach (var member in ent.Members) {
          if (member.Kind == EntityMemberKind.Column) {
            CheckDecimalMember(member); 
            member.ValueIndex = ent.PersistentValuesCount++;
            if (member.Flags.IsSet(EntityMemberFlags.PrimaryKey))
              member.Flags |= EntityMemberFlags.NoDbUpdate;
            if (!member.Flags.IsSet(EntityMemberFlags.NoDbUpdate))
              hasUpdatableMembers = true;
          } else
            member.ValueIndex = ent.TransientValuesCount++;
          if (member.Kind == EntityMemberKind.EntityRef) {
            member.ReferenceInfo.CountCommand = SelectCommandBuilder.BuildGetCountForEntityRef(this.Model, member.ReferenceInfo);
          }
        }//foreach member
        if (!hasUpdatableMembers)
          ent.Flags |= EntityFlags.NoUpdate;
        ent.RefMembers = ent.Members.Where(m => m.Kind == EntityMemberKind.EntityRef).ToList();
        ent.RowVersionMember = ent.Members.FirstOrDefault(m => m.Flags.IsSet(EntityMemberFlags.RowVersion));
        // set ReferencesIdentity flag
        foreach(var refM in ent.RefMembers)
          if (refM.ReferenceInfo.ToKey.Entity.Flags.IsSet(EntityFlags.HasIdentity))
            ent.Flags |= EntityFlags.ReferencesIdentity;
        ent.AllMembersMask = new EntityMemberMask(ent.PersistentValuesCount, setAll: true);
      }//foreach ent
    }

    private void CheckDecimalMember(EntityMemberInfo member) {
      //assign default prec and scale for decimal members - (18, 4), commonly used for money values
      if (member.DataType == typeof(decimal) && member.DataType == typeof(decimal?) && member.Precision == 0 && member.Scale == 0) {
        member.Precision = 18;
        member.Scale = 4;
      }
    }

    private void BuildEntityClasses() {
      _app.EntityClassProvider.SetupEntityClasses(this.Model);
    }

    private void CollectEnumTypes() {
      var typeSet = new HashSet<Type>(); 
      foreach (var ent in Model.Entities)
        foreach (var member in ent.Members) {
          var type = member.DataType; 
          if (type.IsEnum && !typeSet.Contains(type)) {
            typeSet.Add(type);
            Model.AddEnumType(type);
          }
        }//foreach member
    }



    // Note about special case: members with CascadeDelete attribute.
    // Demo case setup. 3 entities, IBook, IAuthor, and IBookAuthor as link table; IBookAuthor references IBook with CascadeDelete,
    // and references IAuthor without cascade. 
    // Because of CascadeDelete, when we delete IBook and IBookAuthor in one operation, the order of IBook vs IBookAuthor does not matter: 
    // even if IBook comes before IBookAuthor, delete will succeed because of cascade delete of IBookAuthor. 
    // The problem case is when we are deleting IBook and IAuthor, without explicitly deleting IBookAuthor. 
    // In this case IAuthor should be deleted after IBook - otherwise still existing IBookAuthor record
    // would prevent it from deleting. As there's no explicit IBookAuthor in delete set, and there's 
    // no FK links between IAuthor and IBook - then they may come to delete in any order, and trans might fail.
    // The solution is to introduce an extra direct link between IBook and IAuthor in abstract SCC node tree.
    // This extra link will ensure proper topological ordering of IBook and IAuthor.
    // Note that we still need to add link between IBookAuthor and IBook - for proper ordering of inserts.
    private void ComputeTopologicalIndexes() {
      // Run SCC algorithm
      var g = new SccGraph();
      //Perform SCC analysis.
      foreach (var ent in Model.Entities)
        ent.SccVertex = g.Add(ent);
      //setup links
      foreach (var ent in Model.Entities) {
        var cascadeMembers = new List<EntityMemberInfo>();
        var nonCascadeMembers = new List<EntityMemberInfo>(); 
        foreach (var member in ent.RefMembers) {
          var targetEnt = member.ReferenceInfo.ToKey.Entity;
            ent.SccVertex.AddLink(targetEnt.SccVertex);
            if (member.Flags.IsSet(EntityMemberFlags.CascadeDelete))
              cascadeMembers.Add(member);
            else
              nonCascadeMembers.Add(member); 
        }//foreach member
        //For all cascade member (IBookAuthor.Author) targets add direct links to all non-cascade member targets 
        // (from IBook to IAuthor)
        foreach (var cascMember in cascadeMembers) {
          var cascTarget = cascMember.ReferenceInfo.ToKey.Entity;
          foreach (var nonCascMember in nonCascadeMembers) {
            var nonCascTarget = nonCascMember.ReferenceInfo.ToKey.Entity;
            cascTarget.SccVertex.AddLink(nonCascTarget.SccVertex);
          }
        }//foreach cascMember
      }//foreach ent

      //Build SCC
      var sccCount = g.BuildScc();
      //read scc index and clear vertex fields
      foreach (var ent in Model.Entities) { 
        var v = ent.SccVertex;
        ent.TopologicalIndex = v.SccIndex;
        if (v.NonTrivialGroup)
          ent.Flags |= EntityFlags.TopologicalGroupNonTrivial;
        ent.SccVertex = null;
      }
    }

    //Builds entity.InitialValues array that will be used to initialize new entities
    private static byte[] _zeroBytes = new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 };

    private void BuildDefaultInitialValues() {
      foreach (var entity in Model.Entities) {
        entity.InitialColumnValues = new object[entity.PersistentValuesCount];
        foreach (var member in entity.Members)
          switch (member.Kind) {
            case EntityMemberKind.Column:
              object dftValue; 
              if (member.Flags.IsSet(EntityMemberFlags.ForeignKey)) 
                dftValue = DBNull.Value;
              else if (member.AutoValueType == AutoType.RowVersion) {
                dftValue = _zeroBytes; 
              } else 
                dftValue = member.DataType.IsValueType ? Activator.CreateInstance(member.DataType) : DBNull.Value;
              member.DefaultValue = member.DefaultValue = dftValue;
              entity.InitialColumnValues[member.ValueIndex] = dftValue;
              break; 
            case EntityMemberKind.Transient:
              member.DefaultValue = member.DeniedValue = null;
              break; 
            case EntityMemberKind.EntityRef:
              member.DefaultValue = member.DeniedValue = DBNull.Value;
              break; 
            case EntityMemberKind.EntityList:
              member.DefaultValue = null;
              member.DeniedValue = ReflectionHelper.CreateReadOnlyCollection(member.ChildListInfo.TargetEntity.EntityType);
              break; 
          }//switch
        
      } // foreach entity
    }


  }//class

}
