﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Reflection;
using Vita.Entities.Utilities;

using Vita.Entities.Model;
using Vita.Entities.Model.Construction;
using Vita.Entities.Logging;
using Vita.Entities.Runtime;
using Vita.Entities.Services;

namespace Vita.Entities {
  // Special framework attributes, known to core framework and applied using specific internal logic

  public partial class EntityAttribute {
    public override void ApplyOnEntity(EntityModelBuilder builder) {
      if(!string.IsNullOrWhiteSpace(this.Name))
        HostEntity.Name = this.Name;
      HostEntity.TableName = this.TableName;
    }
  } //class

  public partial class NoColumnAttribute {
    public override void ApplyOnMember(EntityModelBuilder builder) {
      HostMember.Kind = EntityMemberKind.Transient;
      HostMember.GetValueRef = MemberValueGettersSetters.GetTransientValue;
      HostMember.SetValueRef = MemberValueGettersSetters.SetTransientValue;
    }
  }// class

  public partial class ComputedAttribute {
    private MethodInfo _method;

    public override void Validate(IActivationLog log) {
      base.Validate(log);
      if(HostMember.ClrMemberInfo.HasSetter())
        log.Error("Computed property {0}.{1} may not have a setter, it is readonly.",
          HostEntity.EntityType, HostMember.MemberName);
    }

    public override void ApplyOnMember(EntityModelBuilder builder) {
      if(!this.Persist)
        HostMember.Kind = EntityMemberKind.Transient;
      HostMember.Flags |= EntityMemberFlags.Computed;
      var bFlags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
      _method = this.MethodClass.GetMethod(this.MethodName, bFlags);
      if(_method == null) {
        builder.Log.Error("Method {0} for computed column {1} not found in type {2}",
          this.MethodName, HostMember.MemberName, this.MethodClass);
        return;
      }
      HostMember.GetValueRef = GetComputedValue;
      HostMember.SetValueRef = MemberValueGettersSetters.DummySetValue;
    }

    public object GetComputedValue(EntityRecord rec, EntityMemberInfo member) {
      if(_method == null)
        return null;
      var value = _method.Invoke(null, new object[] { rec.EntityInstance });
      if(this.Persist && rec.Status != EntityStatus.Loaded)
        rec.SetValueDirect(member, value);
      return value;
    }

  }// class

  public partial class PersistOrderInAttribute {
    public override void Validate(IActivationLog log) {
      base.Validate(log);
      if(HostMember.Kind != EntityMemberKind.EntityList)
        log.Error("PersistOrderIn attribute may be specified only on list members. Member: {0}.{1}.",
          HostEntity.EntityType, HostMember.MemberName);
    }

    public override void ApplyOnMember(EntityModelBuilder builder) {
      EntityInfo orderedEntity = null;
      //determine the entity that is ordered
      switch(HostMember.ChildListInfo.RelationType) {
        case EntityRelationType.ManyToOne:
          orderedEntity = HostMember.ChildListInfo.TargetEntity;
          break;
        case EntityRelationType.ManyToMany:
          orderedEntity = HostMember.ChildListInfo.LinkEntity;
          break;
      }
      //check that there is a member 
      var orderMember = orderedEntity.GetMember(this.Property);
      if(orderMember == null) {
        builder.Log.Error("Property '{0}' referenced in PersistOrderIn attribute on entity {1} not found in entity {2}.",
          this.Property, HostEntity.Name, orderedEntity.Name);
        return;
      }
      //current limitation - index property must be int32 only
      if(orderMember.DataType != typeof(Int32)) {
        builder.Log.Error("Invalid data type ({0}) for property '{1}' referenced in PersistOrderIn attribute on entity {2}: must be Int32.",
          orderMember.DataType.Name, this.Property, HostEntity.EntityType);
        return;
      }
      // Validation passed, assign order member
      HostMember.ChildListInfo.PersistentOrderMember = orderMember;
      // Make list order to be by orderMember
      var listInfo = HostMember.ChildListInfo;
      listInfo.OrderBy = new List<EntityKeyMemberInfo>();
      listInfo.OrderBy.Add(new EntityKeyMemberInfo(orderMember, desc: false));
    }
  }

  public partial class OrderByAttribute {

    public override void Validate(IActivationLog log) {
      base.Validate(log);
      if(HostMember != null && HostMember.Kind != EntityMemberKind.EntityList) {
        log.Error("OrderBy attribute may be used only on entities or list properties. Property: {0}", this.GetHostRef());
      }
    }

    public override void ApplyOnEntity(EntityModelBuilder builder) {
      if(HostEntity.DefaultOrderBy != null) {
        builder.Log.Error("More than one OrderBy attribute in entity {0}.", HostEntity.Name);
      }
      if(!EntityModelBuilderHelper.TryParseKeySpec(HostEntity, this.OrderByList, builder.Log, out HostEntity.DefaultOrderBy,
        ordered: true, specHolder: HostEntity))
        return;
      //Check that they are real cols
      foreach(var ordM in HostEntity.DefaultOrderBy) {
        if(ordM.Member.Kind != EntityMemberKind.Column)
          builder.Log.Error("Invalid property {0} in OrderBy attribute in entity {1} - must be a simple value column.",
            ordM.Member.MemberName, HostEntity.Name);
      }
    }//method

    //This is a special case - OrderBy attribute specifies the order of entities in list property.
    public override void ApplyOnMember(EntityModelBuilder builder) {
      var entity = HostEntity;
      var listInfo = HostMember.ChildListInfo;
      EntityModelBuilderHelper.TryParseKeySpec(HostMember.ChildListInfo.TargetEntity, this.OrderByList, builder.Log,
           out listInfo.OrderBy, ordered: true, specHolder: HostEntity);
    }


  }// class

  public partial class PropertyGroupAttribute {

    public override void Validate(IActivationLog log) {
      base.Validate(log);
      if(string.IsNullOrWhiteSpace(this.GroupName))
        log.Error("Group name may not be empty. Entity: {0}.", HostEntity.Name);
    }

    public override void ApplyOnEntity(EntityModelBuilder builder) {
      var names = StringHelper.SplitNames(this.MemberNames);
      foreach(var name in names) {
        var member = HostEntity.GetMember(name);
        if(member == null) {
          builder.Log.Error("PropertyGroup '{0}', entity {1}: member {2} not found.", this.GroupName, HostEntity.Name, name);
          return;
        }
        var grp = HostEntity.GetPropertyGroup(this.GroupName, create: true);
        if(!grp.Members.Contains(member))
          grp.Members.Add(member);
      }//foreach
    }
  } //class

  public partial class GroupsAttribute {
    public override void Validate(IActivationLog log) {
      base.Validate(log);
      if(string.IsNullOrWhiteSpace(this.GroupNames))
        log.Error("Groups value may not be empty, entity {0}.", HostEntity.EntityType);
    }

    public override void ApplyOnMember(EntityModelBuilder builder) {
      var names = StringHelper.SplitNames(this.GroupNames);
      foreach(var name in names) {
        if(string.IsNullOrWhiteSpace(name))
          continue;
        var grp = HostEntity.GetPropertyGroup(name, create: true);
        if(!grp.Members.Contains(HostMember))
          grp.Members.Add(HostMember);
      }//foreach
    }
  } //class

  public partial class NoUpdateAttribute {
    public override void ApplyOnMember(EntityModelBuilder builder) {
      HostMember.Flags |= EntityMemberFlags.NoDbUpdate;
    }
  }// class

  public partial class ReadOnlyAttribute {
    public override void ApplyOnMember(EntityModelBuilder builder) {
      HostMember.Flags |= EntityMemberFlags.NoDbInsert | EntityMemberFlags.NoDbUpdate;
    }
  }// class

  public partial class DateOnlyAttribute {
    private Action<EntityRecord, EntityMemberInfo, object> _defaultSetter;

    public override void Validate(IActivationLog log) {
      base.Validate(log);
      var dataType = HostMember.DataType;
      if(HostMember.DataType != typeof(DateTime) && HostMember.DataType != typeof(DateTime?))
        log.Error("Property {0}.{1}: DateOnly attribute may be specified only on DataTime properties. ",
          HostEntity.Name, HostMember.MemberName);
    }

    public override void ApplyOnMember(EntityModelBuilder builder) {
      //Inject interceptor
      _defaultSetter = HostMember.SetValueRef;
      HostMember.SetValueRef = this.SetValueDateTime;
    }

    //Interceptors for SetValue
    void SetValueDateTime(EntityRecord record, EntityMemberInfo member, object value) {
      if(value != null && value != DBNull.Value) {
        if(value.GetType() == typeof(DateTime?))
          value = (DateTime?)((DateTime?)value).Value.Date;
        else
          value = ((DateTime)value).Date;
      }// if value != null
      _defaultSetter(record, member, value);
    }

  }// class

  public partial class UtcAttribute {
    Func<EntityRecord, EntityMemberInfo, object> _defaultGetter;
    Action<EntityRecord, EntityMemberInfo, object> _defaultSetter;

    public override void Validate(IActivationLog log) {
      base.Validate(log);
      var dataType = HostMember.DataType;
      if(HostMember.DataType != typeof(DateTime) && HostMember.DataType != typeof(DateTime?))
        log.Error("Property {0}.{1}: Utc attribute may be specified only on DataTime properties. ",
          HostEntity.Name, HostMember.MemberName);
    }

    public override void ApplyOnMember(EntityModelBuilder builder) {
      HostMember.Flags |= EntityMemberFlags.Utc;
      //Inject interceptor
      _defaultGetter = HostMember.GetValueRef;
      _defaultSetter = HostMember.SetValueRef;
      HostMember.GetValueRef = GetValueInterceptor;
      HostMember.SetValueRef = SetValueInterceptor;
    }
    //Interceptor for SetValue
    void SetValueInterceptor(EntityRecord record, EntityMemberInfo member, object value) {
      var utcValue = ToUtc(value);
      _defaultSetter(record, member, utcValue);
    }

    object GetValueInterceptor(EntityRecord record, EntityMemberInfo member) {
      var value = _defaultGetter(record, member);
      var utcValue = ToUtc(value);
      return utcValue;
    }

    private object ToUtc(object value) {
      if(value == null || value == DBNull.Value)
        return value;
      DateTime dtValue;
      if(value.GetType() == typeof(DateTime?))
        dtValue = ((DateTime?)value).Value;
      else
        dtValue = (DateTime)value;
      // If value is coming from database or from serialization, its Kind shows Unspecified - set it to UTC explicitly; as we know that it is in fact UTC
      //if (record.Status == EntityStatus.Loading && dtValue.Kind == DateTimeKind.Unspecified)
      switch(dtValue.Kind) {
        case DateTimeKind.Utc:
          return dtValue;
        case DateTimeKind.Local:
          return dtValue.ToUniversalTime();
        case DateTimeKind.Unspecified:
          return DateTime.SpecifyKind(dtValue, DateTimeKind.Utc); // assume it is already UTC
        default:
          return dtValue; // just to supress compiler error
      }
    }
  }// class


  public partial class UnlimitedAttribute {
    public override void ApplyOnMember(EntityModelBuilder builder) {
      HostMember.Flags |= EntityMemberFlags.UnlimitedSize;
      HostMember.Size = -1;
    }

  }// class


  public partial class DependsOnAttribute {
    public override void ApplyOnMember(EntityModelBuilder builder) {
      var namesArr = StringHelper.SplitNames(this.MemberNames);
      foreach(var name in namesArr) {
        var targetMember = HostEntity.GetMember(name);
        if(targetMember == null) {
          builder.Log.Error("Member {0} referenced in DependsOn attribute on member {1}.{2} not found.", name,
            HostEntity.Name, HostMember.MemberName);
          return;
        }
        //add this member to DependentMembers array of targetMember
        if(targetMember.DependentMembers == null)
          targetMember.DependentMembers = new EntityMemberInfo[] { HostMember };
        else {
          var mList = targetMember.DependentMembers.ToList();
          mList.Add(HostMember);
          targetMember.DependentMembers = mList.ToArray();
        }
      }//foreach name
    } // method
  }//class

  public partial class ValidateAttribute {
    MethodInfo _method;

    public override void ApplyOnEntity(EntityModelBuilder builder) {
      _method = this.MethodClass.GetMethod(this.MethodName);
      if(_method == null) {
        builder.Log.Error("Method {0} specified as Validation method for entity {1} not found in type {2}",
            this.MethodName, HostEntity.EntityType, this.MethodClass);
        return;
      }
      HostEntity.Events.ValidatingChanges += Events_Validating;
    }

    void Events_Validating(EntityRecord record, EventArgs args) {
      _method.Invoke(null, new object[] { record.EntityInstance });
    }
  }// class

  public partial class PropagateUpdatedOnAttribute {
    Action<EntityRecord, EntityMemberInfo, object> _defaultValueSetter;

    public override void Validate(IActivationLog log) {
      base.Validate(log);
      if(HostMember.Kind != EntityMemberKind.EntityRef)
        log.Error(
          "PropagateUpdatedOn attribute may be used only on properties that are references to other entities. Property: {0}",
            GetHostRef());
    }

    public override void ApplyOnMember(EntityModelBuilder builder) {
      //Set interceptors
      HostMember.Entity.Events.Modified += Events_ModifiedDeleted;
      HostMember.Entity.Events.Deleting += Events_ModifiedDeleted;
      _defaultValueSetter = HostMember.SetValueRef;
      HostMember.SetValueRef = SetMemberValue;
    }

    private void Events_ModifiedDeleted(EntityRecord record, EventArgs args) {
      MarkTargetAsModified(record);
    }

    private void SetMemberValue(EntityRecord record, EntityMemberInfo member, object value) {
      _defaultValueSetter(record, member, value);
      MarkTargetAsModified(record);
    }
    private void MarkTargetAsModified(EntityRecord record) {
      var rec = record;
      var target = rec.GetValue(HostMember);
      if(target == null)
        return;
      var targetRec = EntityHelper.GetRecord(target);
      if(targetRec.Status == EntityStatus.Loaded)
        targetRec.Status = EntityStatus.Modified;
    }
  }


  public partial class HashForAttribute {
    EntityMemberInfo _hashedMember;
    Action<EntityRecord, EntityMemberInfo, object> _oldSetter;
    IHashingService _hashingService;

    public override void Validate(IActivationLog log) {
      base.Validate(log);
      if(string.IsNullOrEmpty(this.PropertyName))
        log.Error("HashFor attribute - PropertyName must be specified. Entity/property: {0}.{1}.",
          HostEntity.Name, HostMember.MemberName);
    }

    public override void ApplyOnMember(EntityModelBuilder builder) {
      if(HostMember.DataType != typeof(int)) {
        builder.Log.Error("HashFor attribute can be used only on int properties. Entity/property: {0}.{1}.",
          HostEntity.Name, HostMember.MemberName);
        return;
      }
      _hashedMember = HostEntity.GetMember(this.PropertyName);
      if(_hashedMember == null) {
        builder.Log.Error("Property {0} referenced in HashFor attribute on property {1} not found on entity {2}.",
          this.PropertyName, HostMember.MemberName, HostEntity.Name);
        return;
      }
      if(_hashedMember.DataType != typeof(string)) {
        builder.Log.Error("HashFor attribute on property {0}.{1}: target property must be of string type.",
          HostEntity.Name, HostMember.MemberName);
        return;
      }
      _oldSetter = _hashedMember.SetValueRef;
      _hashedMember.SetValueRef = OnSettingValue;
      _hashingService = builder.Model.App.GetService<IHashingService>();
    }

    public void OnSettingValue(EntityRecord record, EntityMemberInfo member, object value) {
      var rec = record;
      _oldSetter(rec, member, value);
      if(record.Status == EntityStatus.Loading)
        return; //we are loading from db 
      var strValue = (string)value;
      var hash = _hashingService.ComputeHash(strValue);
      rec.ValuesModified[HostMember.ValueIndex] = hash;
    }
  }

  public partial class SecretAttribute {
    public override void ApplyOnMember(EntityModelBuilder builder) {
      HostMember.Flags |= EntityMemberFlags.Secret | EntityMemberFlags.NoDbUpdate; //updates are possible only thru custom update method
      HostMember.GetValueRef = GetSecretValue;
    }

    private object GetSecretValue(EntityRecord rec, EntityMemberInfo member) {
      var record = rec;
      //Always return value from Modified values - this value just set by the code; but no way to read value from database (which is in OriginalValues)
      var value = record.ValuesModified[member.ValueIndex];
      if(value == null)
        value = member.DeniedValue;
      if(value == DBNull.Value)
        return null;
      return value;
    }
  }

  public partial class DiscardOnAbortAttribute {
    public override void ApplyOnEntity(EntityModelBuilder builder) {
      HostEntity.Flags |= EntityFlags.DiscardOnAbourt;
    }
  }

  public partial class DisplayAttribute {
    private MethodInfo _customMethodInfo;
    private string _adjustedFormat;
    private string[] _propNames;

    public override void ApplyOnEntity(EntityModelBuilder builder) {
      if(this.MethodClass != null && this.MethodName != null) {
        _customMethodInfo = this.MethodClass.GetMethod(MethodName);
        if(_customMethodInfo == null) {
          builder.Log.Error("Method {0} specified as Display method for entity {1} not found in type {2}",
              MethodName, HostEntity.EntityType, this.MethodClass);
          return;
        }
        HostEntity.DisplayMethod = InvokeCustomDisplay;
        return;
      }
      //Check if Format provided
      if(string.IsNullOrEmpty(this.Format)) {
        builder.Log.Error("Invalid Display attribute on entity {0}. You must provide method reference or non-empty Format value.",
            HostEntity.EntityType);
        return;
      }
      //Parse Format value, build argIndexes from referenced property names
      StringHelper.TryParseTemplate(this.Format, out _adjustedFormat, out _propNames);
      //verify and build arg indexes
      foreach(var prop in _propNames) {
        //it might be dotted sequence of props; we check only first property
        var propSeq = prop.SplitNames('.');
        var member = HostEntity.GetMember(propSeq[0]);
        if(member == null) {
          builder.Log.Error("Invalid Format expression in Display attribute on entity {0}. Property {1} not found.",
            HostEntity.EntityType, propSeq[0]);
          return;
        }
      }//foreach
      HostEntity.DisplayMethod = GetDisplayString;
    }

    // we might have secure session, so elevate read to allow access
    private string InvokeCustomDisplay(EntityRecord record) {
      if(Disabled)
        return "(DisplayDisabled)";
      using(record.Session.WithElevatedRead()) {
        return (string)_customMethodInfo.Invoke(null, new object[] { record.EntityInstance });
      }
    }

    private string GetDisplayString(EntityRecord record) {
      if(Disabled)
        return "(DisplayDisabled)";
      if(_propNames == null)
        return this.Format;
      var args = new object[_propNames.Length];
      for(int i = 0; i < args.Length; i++)
        args[i] = GetPropertyChainValue(record, _propNames[i]);
      return Util.SafeFormat(_adjustedFormat, args);
    }

    private object GetPropertyChainValue(EntityRecord record, string propertyChain) {
      var rec = record;
      using(rec.Session.ElevateRead()) {
        if(!propertyChain.Contains('.'))
          return rec.GetValue(propertyChain);
        var props = propertyChain.SplitNames('.');
        var currRec = rec;
        object result = null;
        foreach(var prop in props) {
          result = currRec.GetValue(prop);
          if(result is EntityBase)
            currRec = EntityHelper.GetRecord(result);
          else
            return result; // stop as soon as we reach non-entity
        }
        return result;
      } //using
    } //method

    public static bool Disabled; //for debugging
  }//class

  public partial class CascadeDeleteAttribute {

    public override void Validate(IActivationLog log) {
      base.Validate(log);
      if(HostMember.Kind != EntityMemberKind.EntityRef)
        log.Error("CascadeDelete attribute may be used only on properties that are references to other entities. Property: {0}.",
          GetHostRef());
    }
    public override void ApplyOnMember(EntityModelBuilder builder) {
      HostMember.Flags |= EntityMemberFlags.CascadeDelete;
    }
  }

  public partial class AsIsAttribute {
    public override void ApplyOnMember(EntityModelBuilder builder) {
      HostMember.Flags |= EntityMemberFlags.AsIs;
    }
  }

  public partial class BypassAuthorizationAttribute {
    public override void ApplyOnEntity(EntityModelBuilder builder) {
      HostEntity.Flags |= EntityFlags.BypassAuthorization;
    }
  }

  public partial class GrantAccessAttribute {
    public override void Validate(IActivationLog log) {
      base.Validate(log);
      if(HostMember.Kind != EntityMemberKind.EntityRef)
        log.Error(
          "GrantAccess attribute may be used only on properties that are references to other entities. Property: {0}",
            GetHostRef());
    }

    public override void ApplyOnMember(EntityModelBuilder builder) {
      var targetEnt = HostMember.ReferenceInfo.ToKey.Entity;
      // _member.ByRefPermissions = UserRecordPermission.Create(targetEnt, this.Properties, this.AccessType);
    }

  }


  public partial class ColumnAttribute {
    public override void ApplyOnMember(EntityModelBuilder builder) {
      if(!string.IsNullOrWhiteSpace(this.ColumnName))
        HostMember.ColumnName = this.ColumnName;
      if (!string.IsNullOrWhiteSpace(this.Default))
        HostMember.ColumnDefault = this.Default;
      HostMember.Scale = this.Scale;
      if (this.Precision > 0)
        HostMember.Precision = this.Precision;
      if (this.Size != 0)
        HostMember.Size = this.Size;
      HostMember.ExplicitDbTypeSpec = this.DbTypeSpec?.ToLowerInvariant();
    }

  }// class

  public partial class SizeAttribute {
    public override void ApplyOnMember(EntityModelBuilder builder) {
      if((this.Options & SizeOptions.AutoTrim) != 0)
        HostMember.Flags |= EntityMemberFlags.AutoTrim;
      // Check size code and lookup in tables
      if(!string.IsNullOrEmpty(this.SizeCode)) {
        var sizeTable = builder.Model.App.SizeTable;
        //If there is size code, look it up in SizeTable; first check module-specific value, then global value for the code
        int size;
        //check full code with module's namespace prefix or short size code
        var fullCode = Sizes.GetFullSizeCode(HostMember.Entity.EntityType.Namespace, this.SizeCode); 
        if (!sizeTable.TryGetValue(fullCode, out size) && !sizeTable.TryGetValue(this.SizeCode, out size)) {
          builder.Log.Error("Unknown size code '{0}', entity/member: {1}.{2}", this.SizeCode, HostEntity.Name, HostMember.MemberName);
          return; 
        } 
        HostMember.Size = size;
        return; 
      }
      //If size is specified explicitly, use it
      if(this.Size > 0) {
        HostMember.Size = this.Size;
        return;
      }
    }
  }// class

  public partial class NullableAttribute {

    public override void ApplyOnMember(EntityModelBuilder builder) {
      HostMember.Flags |= EntityMemberFlags.Nullable;
      if (HostMember.DataType.IsValueType) {
        HostMember.Flags |= EntityMemberFlags.ReplaceDefaultWithNull;
        HostMember.GetValueRef = MemberValueGettersSetters.GetValueTypeReplaceNullWithDefault;
        HostMember.SetValueRef = MemberValueGettersSetters.SetValueTypeReplaceDefaultWithNull;
      }
    }
  }// class


  public partial class OldNamesAttribute {
    public override void ApplyOnEntity(EntityModelBuilder builder) {
      var names = StringHelper.SplitNames(this.OldNames);
      // add variation without leading 'I'
      var allNames = new List<string>(names);
      foreach(var n in names)
        if(n.Length > 1 && n.StartsWith("I"))
          allNames.Add(n.Substring(1));
      HostEntity.OldNames = allNames.ToArray(); 
    }

    public override void ApplyOnMember(EntityModelBuilder builder) {
      HostMember.OldNames = StringHelper.SplitNames(this.OldNames);
    }

  }// class


  public partial class OneToOneAttribute {
    EntityInfo _targetEntity;

    public override void Validate(IActivationLog log) {
      // We set Kind to Transient to prevent treating the member as regular entity reference by model builder
      HostMember.Kind = EntityMemberKind.Transient;
      base.Validate(log);
    }

    public override void ApplyOnMember(EntityModelBuilder builder) {
      // It is initially assigned EntityRef
      if(!builder.Model.IsEntity(HostMember.DataType)) {
        builder.Log.Error("{0} attribute may be used only on properties that are references to other entities. Property: {1}",
           this.GetType().Name,  GetHostRef());
        return;
      }
      HostMember.Kind = EntityMemberKind.Transient;
      HostMember.Flags |= EntityMemberFlags.FromOneToOneRef;
      _targetEntity = builder.Model.GetEntityInfo(HostMember.DataType);
      Util.Check(_targetEntity != null, "Target entity not found: {0}", HostMember.DataType.Name);
      //check that PK of target entity points back to 'this' entity
      var targetPkMembers = _targetEntity.PrimaryKey.KeyMembers;
      var isOk = targetPkMembers.Count == 1 && targetPkMembers[0].Member.DataType == HostEntity.EntityType;
      if(!isOk) {
        builder.Log.Error("OneToOne property {0}: target entity must have Primary key pointing back to entity {0}.", GetHostRef(), HostEntity.EntityType.Name);
        return;
      }
      HostMember.GetValueRef = GetValue;
      HostMember.SetValueRef = SetValue;
    }//method

    object GetValue(EntityRecord record, EntityMemberInfo member) {
      var v = record.GetValueDirect(member);
      if(v != null) {
        if(v == DBNull.Value)
          return null;
        var rec = (EntityRecord)v;
        return rec.EntityInstance;
      }
      //retrieve entity 
      var ent = record.Session.SelectByPrimaryKey(_targetEntity, record.PrimaryKey.Values);
      if (ent == null) {
        record.SetValueDirect(member, DBNull.Value);
        return null;
      }
      var targetRec = ent.Record; 
      record.SetValueDirect(member, targetRec);
      if(targetRec.ByRefUserPermissions == null)
        targetRec.ByRefUserPermissions = member.ByRefPermissions;
      return targetRec.EntityInstance;
    }

    void SetValue(EntityRecord record, EntityMemberInfo member, object value) {
      Util.Throw("OneToOne properties are readonly, cannot set value. Property: {0}", GetHostRef());
    }
  }//attribute

  public partial class TransactionIdAttribute : EntityModelAttributeBase {
    private Guid? _defaultValue;

    public override void ApplyOnMember(EntityModelBuilder builder) {
      base.ApplyOnMember(builder);
      var member = base.HostMember;
      var type = Nullable.GetUnderlyingType(member.DataType) ?? member.DataType;
      if(type != typeof(long) && type != typeof(ulong)) {
        builder.Log.Error("TransactionId attribute may be used only on properties of type long or ulong.");
        return;
      }
      member.Flags |= EntityMemberFlags.IsSystem;
      _defaultValue = (member.DataType == typeof(Guid)) ? Guid.Empty : (Guid?)null;
      member.Entity.SaveEvents.SavingChanges += SaveEvents_SavingChanges;
    }

    void SaveEvents_SavingChanges(EntityRecord record, EventArgs args) {
      if(Action == TrackedAction.Created && record.Status == EntityStatus.New ||
          Action == TrackedAction.Updated && (record.Status == EntityStatus.New || record.Status == EntityStatus.Modified)) {
        //Do it directly, to bypass authorization checks (it should still work with record.SetValue)
        record.ValuesModified[HostMember.ValueIndex] = record.Session.GetNextTransactionId();
      }
    }//method

  }

}
