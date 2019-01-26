﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Linq.Expressions;

using Vita.Entities.Utilities;
using Vita.Entities.Model;
using Vita.Entities.Services;
using Vita.Data;
using Vita.Entities.Locking;
using Vita.Data.Linq;
using Vita.Entities.Logging;
using Vita.Data.Linq.Translation;
using Vita.Data.Runtime;

namespace Vita.Entities.Runtime {

  public enum EntitySessionSubType {
    ReadWrite,
    ReadOnly,
    ConcurrentReadOnly, //allowed only for readonly sessions
  }

  public enum EntitySessionOptions {
    None = 0,
    DisableLog = 1,
    DisableCache = 1 << 1,
    DisableBatchMode = 1 << 2,
  }


  /// <summary> Represents a live connection to the database with tracking of loaded and changed/added/deleted entities. </summary>
  /// <remarks>This class provides methods for data access: reading, updating, deleting entitites. </remarks>
  public partial class EntitySession : IEntitySession {
    public readonly OperationContext Context;
    public readonly ILog Log;
    public EntitySessionSubType Flags;
    public EntitySessionOptions Options;

    public readonly EntitySessionSubType SubType;
    public readonly bool IsReadOnly;
    public readonly bool IsSecureSession;

    public readonly EntityRecordWeakRefTable RecordsLoaded;
    public readonly IList<EntityRecord> RecordsChanged;
    public readonly IList<IPropertyBoundList> ListsChanged;
    public readonly HashSet<EntityRecord> RecordsToClearLists;
    public readonly List<LinqCommand> ScheduledCommands;

    // last executed DbCommand, use EntityHelper.GetLastCommand(this session) to retrieve it.
    public System.Data.IDbCommand LastCommand { get; internal set; }
    public DateTime? LastTransactionDateTime { get; private set; }
    public int LastTransactionDuration;
    public int LastTransactionRecordCount { get; private set; }

    public Guid NextTransactionId; // copied into ITransactionLog.Id

    private ITimeService _timeService;
    private EntityAppEvents _appEvents;
    DataSource _dataSource;

    #region constructor
    public EntitySession(OperationContext context, EntitySessionSubType subType = EntitySessionSubType.ReadWrite,
            EntitySessionOptions options = EntitySessionOptions.None) {
      Context = context;
      SubType = subType;
      Options = options;

      IsSecureSession = false;
      _appEvents = Context.App.AppEvents;
      this.Log = Context.Log;
      // Multithreaded sessions must be readonly
      var isConcurrent = SubType == EntitySessionSubType.ConcurrentReadOnly;
      IsReadOnly = SubType == EntitySessionSubType.ReadOnly || isConcurrent;
      RecordsLoaded = new EntityRecordWeakRefTable(isConcurrent);
      // These lists are not used in Readonly sessions
      if(!IsReadOnly) {
        RecordsChanged = new List<EntityRecord>();
        ListsChanged = new List<IPropertyBoundList>();
        RecordsToClearLists = new HashSet<EntityRecord>();
        ScheduledCommands = new List<LinqCommand>();
      }
      _timeService = Context.App.TimeService;
      //This might be reset in SaveChanges
      NextTransactionId = Guid.NewGuid();
      _dataSource = context.App.DataAccess.GetDataSource(this.Context);
      _appEvents.OnNewSession(this);
    }
    #endregion

    public override string ToString() {
      return "EntitySession(" + Context + ")";
    }

    // Reusable connection object
    public DataConnection CurrentConnection {
      get { return _currentConnection; }
      set {
        if(value == _currentConnection)
          return;
        _currentConnection = value;
        if(_currentConnection != null)
          Context.RegisterDisposable(_currentConnection);
      }
    }
    DataConnection _currentConnection;



    #region IEntitySession Methods ===========================================================================================
    // to use field internally (for perf) but expose property in interface
    OperationContext IEntitySession.Context { get { return this.Context; } }

    public virtual TEntity NewEntity<TEntity>() where TEntity : class {
      try {
        CheckNotReadonly();
        var entityInfo = GetEntityInfo(typeof(TEntity));
        Util.Check(entityInfo.Kind != EntityKind.View, "Entity {0} is a view, cannot create new entity.", typeof(TEntity));
        var newRec = NewRecord(entityInfo);
        return (TEntity)(object)newRec.EntityInstance;
      } catch(Exception ex) {
        this._appEvents.OnError(this.Context, ex);
        throw;
      }
    }

    public virtual TEntity GetEntity<TEntity>(object primaryKeyValue, LoadFlags flags = LoadFlags.Default) where TEntity : class {
      try {
        Util.Check(primaryKeyValue != null, "Session.GetEntity<{0}>(): primary key may not be null.", typeof(TEntity));
        var entityInfo = GetEntityInfo(typeof(TEntity));
        Util.Check(entityInfo.Kind != EntityKind.View, "Cannot use session.GetEntity<TEntity>(PK) method for views. Entity: {0}.", typeof(TEntity));
        //Check if it is an entity key object; if not, it is a "value" (or values) of the key
        var pkType = primaryKeyValue.GetType();
        EntityKey pk = entityInfo.CreatePrimaryKeyInstance(primaryKeyValue);
        var rec = GetRecord(pk, flags);
        if(rec != null)
          return (TEntity)(object)rec.EntityInstance;
        return default(TEntity);
      } catch(Exception ex) {
        this._appEvents.OnError(this.Context, ex);
        throw;
      }
    }

    public virtual void DeleteEntity<TEntity>(TEntity entity) where TEntity : class {
      try {
        CheckNotReadonly();
        var record = EntityHelper.GetRecord(entity);
        DeleteRecord(record);
      } catch(Exception ex) {
        this._appEvents.OnError(this.Context, ex);
        throw;
      }
    }

    public virtual bool CanDeleteEntity<TEntity>(TEntity entity, out Type[] blockingEntities) where TEntity : class {
      var record = EntityHelper.GetRecord(entity);
      Util.Check(record != null, "CanDeleteEntity: entity parameter is not an Entity.");
      return CanDeleteRecord(record, out blockingEntities);
    }

    private bool IsEmpty(IEnumerable e) {
      var iEnum = e.GetEnumerator();
      return !iEnum.MoveNext();
    }

    public void CancelChanges() {
      CheckNotReadonly();
      foreach(var rec in RecordsChanged) {
        rec.EntityInfo.Events.OnChangesCanceled(rec);
        rec.CancelChanges();
      }
      RecordsChanged.Clear();
      ListsChanged.Clear();
    }
    public bool HasChanges() {
      return this.RecordsChanged.Count > 0 || this.ScheduledCommands.Count > 0;
    }

    public virtual void SaveChanges() {
      CheckNotReadonly();
      LastTransactionDateTime = _timeService.UtcNow;
      LastTransactionRecordCount = RecordsChanged.Count;
      LastTransactionDuration = 0;
      //Invoke on Saving first, to let auto values to be filled in, before validation
      OnSaving();
      if(!_validationDisabled)
        ValidateChanges();
      try {
        LastTransactionRecordCount = RecordsChanged.Count;
        var start = _timeService.ElapsedMilliseconds;
        SubmitChanges();
        LastTransactionDuration = (int)(_timeService.ElapsedMilliseconds - start);
        OnSaved();
        RecordsChanged.Clear();
        ListsChanged.Clear();
      } catch(Exception ex) {
        OnSaveAborted();
        _appEvents.OnError(this.Context, ex);
        throw;
      }
      NextTransactionId = Guid.NewGuid();
      _transationTags = null;
    }//method

    protected virtual void SubmitChanges() {
      if(this.CurrentConnection == null && !this.HasChanges())
        return;
      _dataSource.SaveChanges(this);
    }

    public IQueryable<TEntity> EntitySet<TEntity>() where TEntity : class {
      return CreateEntitySet<TEntity>(LockType.None);
    }

    internal protected virtual IQueryable<TEntity> CreateEntitySet<TEntity>(LockType lockType) where TEntity : class {
      var entInfo = GetEntityInfo(typeof(TEntity));
      var prov = new EntityQueryProvider(this);
      var entSet = new EntitySet<TEntity>(prov, lockType);
      //Check filters
      var pred = Context.QueryFilter.GetPredicate<TEntity>();
      if(pred != null)
        return entSet.Where(pred.Where);
      else
        return entSet;
    }

    internal IQueryable<INullEntity> CreateDbContextEntitySet() {
      var prov = new EntityQueryProvider(this);
      var entSet = new EntitySet<INullEntity>(prov);
      return entSet; 
    }
    #endregion 

    #region Record manipulation methods
    public virtual EntityRecord GetRecord(EntityKey primaryKey, LoadFlags flags = LoadFlags.Default) {
      if (primaryKey == null || primaryKey.IsNull())
        return null; 
      var record = GetLoadedRecord(primaryKey);
      if (record != null) {
        if (record.Status == EntityStatus.Stub && flags.IsSet(LoadFlags.Load)) record.Reload(); 
        return record; 
      }
      // TODO: review returning Record stub in the context of SecureSession. 
      // We might have security hole here if user is not authorized to access the record  
      if (flags.IsSet(LoadFlags.Stub))
        return CreateStub(primaryKey);
      if (!flags.IsSet(LoadFlags.Load))
        return null; 
      //Otherwise, load it
      var ent = this.SelectByPrimaryKey(primaryKey.KeyInfo.Entity, primaryKey.Values);
      return ent?.Record; 
    }
    public virtual EntityRecord NewRecord(EntityInfo entityInfo) {
      var record = new EntityRecord(entityInfo, EntityStatus.New);
      record = Attach(record);
      record.EntityInfo.Events.OnNew(record);
      return record;
    }

    public void DeleteRecord(EntityInfo entity, EntityKey primaryKeyValue) {
      var rec = GetRecord(primaryKeyValue, LoadFlags.Stub);
      if (rec == null) return;
      DeleteRecord(rec);
    }

    public virtual bool CanDeleteRecord(EntityRecord record, out Type[] blockingEntities) {
      Util.Check(record != null, "CanDeleteRecord: record parameter may not be null.");
      blockingEntities = null; 
      var entInfo = record.EntityInfo;
      var blockingTypeSet = new HashSet<Type>();
      //check all external ref members
      foreach (var refMember in entInfo.IncomingReferences) {
        // if it is cascading delete, this member is not a problem
        if (refMember.Flags.IsSet(EntityMemberFlags.CascadeDelete)) continue;
        var countCmdInfo = refMember.ReferenceInfo.CountCommand;
        var countCmd = new LinqCommand(countCmdInfo, refMember.ReferenceInfo.ToKey.Entity, new object[] {this, record.EntityInstance});
        var count = ExecuteLinqCommand(countCmd);
        var intCount = (count.GetType() == typeof(int)) ? (int)count : (int) Convert.ChangeType(count, typeof(int));          
        if (intCount > 0)
          blockingTypeSet.Add(refMember.Entity.EntityType);
      }
      if (blockingTypeSet.Count == 0)
        return true;
      blockingEntities = blockingTypeSet.ToArray();
      return false; 
    }

    public virtual void DeleteRecord(EntityRecord record) {
      Util.Check(record != null, "DeleteRecord: record parameter may not be null.");
      if(record.Status == EntityStatus.New) {
        RecordsChanged.Remove(record);
        record.Status = EntityStatus.Fantom;
      }
      record.MarkForDelete();
      record.EntityInfo.Events.OnDeleting(record);
    }

    public virtual EntityRecord Attach(EntityRecord record) {
      if (record == null) return null;
      if (record.IsAttached && record.Session == this) 
        return record; //already attached
      record.Session = this;
      if (record.EntityInfo.Kind == EntityKind.View && record.EntityInfo.PrimaryKey == null) { // for view records
        record.Status = EntityStatus.Loaded; 
        return record;
      }
      //Actually attach
      record.IsAttached = true; 
      var oldRecord = record; //might switch to the record already loaded in session
      //Add record to appropriate collection
      switch (record.Status) {
        case EntityStatus.Loading:
        case EntityStatus.Stub:
        case EntityStatus.Loaded:
          record.Status = EntityStatus.Loaded;
          oldRecord = RecordsLoaded.Add(record); //might return existing record
          if (oldRecord != record) { // it is brand new record just loaded
            oldRecord.CopyOriginalValues(record);
            oldRecord.ClearEntityRefValues();
          }
          record.EntityInfo.Events.OnLoaded(record);
          break; 
        case EntityStatus.New:
          RecordsChanged.Add(record);
          break;
        case EntityStatus.Deleting:
        case EntityStatus.Modified:
          oldRecord = RecordsLoaded.Add(record);
          RecordsChanged.Add(oldRecord);
          break; 
      }
      //Just in case, if 'record' is freshly loaded from database, and we have already the same 'oldRecord' in session, 
      // refresh the column values
      if (oldRecord != record) {
        oldRecord.CopyOriginalValues(record);
        oldRecord.ClearEntityRefValues(); 
      }
      return oldRecord;
    }

    public void NotifyRecordStatusChanged(EntityRecord record, EntityStatus oldStatus) {
      var newStatus = record.Status; 
      if (newStatus == oldStatus)
        return; 
      //check new status
      switch (newStatus) {
        case EntityStatus.Loaded:
          if(oldStatus == EntityStatus.New)
            RecordsLoaded.Add(record);
          break;
        case EntityStatus.New:
        case EntityStatus.Modified:
        case EntityStatus.Deleting:
          CheckNotReadonly(); 
          RecordsChanged.Add(record);
          break; 
        case EntityStatus.Fantom:
          this.RecordsLoaded.TryRemove(record.PrimaryKey);
          break; 
      }
    }
    #endregion

    #region Record Get/Set Value
    // Will be overridden SecureSession
    public virtual object RecordGetMemberValue(EntityRecord record, EntityMemberInfo member) {
      return member.GetValueRef(record, member);
    }
    public virtual void RecordSetMemberValue(EntityRecord record, EntityMemberInfo member, object value) {
      member.SetValueRef(record, member, value);
    }

    #endregion

    #region OnSave methods
    // We might have new entities added on the fly, as we fire events. For example, ChangeTracking hooks to EntityStore and it adds new IChangeTrackEntries.
    // We need to make sure that for all added records we fire the Entity's OnSaving event. (All Auto attributes are processed in this event). 
    // That's the reason for tracking 'done-Counts' and calling the invoke loop twice
    protected virtual void OnSaving() {
      _appEvents.OnSavingChanges(this);
      // We need to invoke OnSaving event for each record and Notify each changed entity list. The event handlers may add extra records, 
      // so we may need to repeat the process for extra records.
      int startListChangedIndex = 0;
      int startRecordsChangedIndex = 0;
      while(true) {
        var oldListsChangedCount = ListsChanged.Count;
        var oldRecordsChangedCount = RecordsChanged.Count;
        //Notify all changed lists - these may add/delete records (many-to-many link records)
        for(int i = startListChangedIndex; i < oldListsChangedCount; i++)
          ListsChanged[i].Notify(BoundListEventType.SavingChanges);
        //Saving event handlers may add/remove changed records, so we need to use 'for-i' loop here
        for(int i = startRecordsChangedIndex; i < oldRecordsChangedCount; i++) {
          var rec = RecordsChanged[i];
          rec.EntityInfo.SaveEvents.OnSavingChanges(rec);
        }
        //Check if we are done, or we need to do it again
        if(oldListsChangedCount == ListsChanged.Count && oldRecordsChangedCount == RecordsChanged.Count)
          return;
        startListChangedIndex = oldListsChangedCount;
        startRecordsChangedIndex = oldRecordsChangedCount;
      }
    }

    protected void OnSaved() {
      //Notify all changed lists
      foreach(var list in ListsChanged)
        list.Notify(BoundListEventType.SavedChanges);
      //Process records
      for(int i = 0; i < this.RecordsChanged.Count; i++) {
        var rec = RecordsChanged[i];
        rec.CommitChanges();
        rec.EntityInfo.SaveEvents.OnSavedChanges(rec);
      }
      // Clear lists
      foreach(var rec in this.RecordsToClearLists)
        rec.ClearTransientValues();
      this.RecordsToClearLists.Clear(); 
      //fire events
      _appEvents.OnSavedChanges(this);
    }

    protected void OnSaveAborted() {
      // we need to iterate down, because we my eliminate records from the list
      for (int i = this.RecordsChanged.Count - 1; i >= 0; i--) {
        var rec = RecordsChanged[i];
        rec.EntityInfo.SaveEvents.OnSaveChangesAborted(rec);
        if (rec.EntityInfo.Flags.IsSet(EntityFlags.DiscardOnAbourt)) {
          // Dynamic records are created on the fly when app submits changes (ex: trans logs). If trans aborted, they must be discarded
          RecordsChanged.RemoveAt(i);
          RecordsLoaded.TryRemove(rec.PrimaryKey); 
        }
      }
      this.RecordsToClearLists.Clear(); 
      _appEvents.OnSaveChangesAborted(this);
    }
    #endregion

    #region private methods
    private EntityRecord CreateStub(EntityKey primaryKey) {
      var rec = new EntityRecord(primaryKey);
      rec.Session = this; 
      RecordsLoaded.Add(rec);
      return rec;
    }
    protected virtual EntityRecord GetLoadedRecord(EntityKey primaryKey) {
      if (primaryKey == null)
        return null;
      //1. Look in the cache
      //var entityInfo = primaryKey.Key.Entity;
      // var rec = Database.Cache.Lookup(entityInfo.EntityType, primaryKey);
      // if(rec != null) return rec;
      //2. Look in the group - if it was previously loaded
      var rec = RecordsLoaded.Find(primaryKey);
      // additionally check RecordsChanged; New records are not included in RecordsLoaded because they do not have PrimaryKes set (yet)
      if (rec == null && RecordsChanged != null && RecordsChanged.Count > 0)
        rec = FindNewRecord(primaryKey); 
      return rec;
    }

    private EntityRecord FindNewRecord(EntityKey primaryKey) {
      var ent = primaryKey.KeyInfo.Entity;
      for(int i=0; i < RecordsChanged.Count; i++) { //for-i loop is faster
        var rec = RecordsChanged[i]; 
        if (rec.Status != EntityStatus.New || rec.EntityInfo != ent) continue; 
        //we found new record of matching entity type. Check keys match. First check if key is loaded
        if (!rec.KeyIsLoaded(primaryKey.KeyInfo)) continue; 
        if (primaryKey.Equals(rec.PrimaryKey)) 
          return rec; 
      }
      return null; 
    }
    
    public virtual IList<TEntity> ToEntities<TEntity>(IEnumerable<EntityRecord> records) {
      var entlist = records.Select(r => (TEntity) r.EntityInstance).ToList();
      return entlist;
    }


    protected internal EntityInfo GetEntityInfo(Type entityType) {
      return Context.App.Model.GetEntityInfo(entityType, throwIfNotFound: true);
    }

    #endregion

    #region command execution

    public virtual object ExecuteLinqCommand(LinqCommand command, bool withIncludes = true) {
      try { 
        var cmdInfo = LinqCommandAnalyzer.Analyze(Context.App.Model, command);
        if(command.ParameterValues == null)
          LinqExpressionHelper.EvaluateCommandParameters(command, this);
        var result = _dataSource.ExecuteLinqCommand(this, command);
        switch(command.Kind) {
          case LinqCommandKind.Select:
            Context.App.AppEvents.OnExecutedSelect(this, command);
            if(withIncludes && (command.Info.Includes?.Count > 0 || Context.HasIncludes()))
              IncludeProcessor.RunIncludeQueries(this, command, result);
            break;
          default:
            Context.App.AppEvents.OnExecutedNonQuery(this, command);
            NextTransactionId = Guid.NewGuid();
            break; 
        }
        return result;
      } catch(Exception ex) {
        ex.AddValue("entity-command", command + string.Empty);
        ex.AddValue("parameters", command.ParameterValues);
        throw;
      }
    }

    internal int ExecuteLinqNonQuery<TEntity>(IQueryable baseQuery, LinqCommandKind dbOperation) {
      Util.CheckParam(baseQuery, nameof(baseQuery));
      var targetEnt = Context.App.Model.GetEntityInfo(typeof(TEntity));
      var command = new LinqCommand(baseQuery.Expression, dbOperation, targetEnt);
      var objResult = this.ExecuteLinqCommand(command);
      return (int)objResult;
    }

    public IEntityRecordContainer SelectByPrimaryKey(EntityInfo entity, object[] keyValues,
            LockType lockType = LockType.None, EntityMemberMask mask = null) {
      var selectQuery = SelectCommandBuilder.BuildSelectByKey(entity.PrimaryKey, lockType, null, mask); 
      var cmd = new LinqCommand(selectQuery, entity, keyValues);
      var list = (IList) ExecuteLinqCommand(cmd);
      if(list.Count == 0)
        return null;
      return (IEntityRecordContainer)list[0]; 
    }

    #endregion

    #region Validation
    public virtual void ValidateChanges() {
      // important - use for-i loop; validation may modify records (and add to this list)
      for( int i = 0; i < RecordsChanged.Count; i++)
        ValidateRecord(RecordsChanged[i]);
      Context.ThrowValidation();
    }

    public bool ValidateRecord(EntityRecord record) {
      record.ValidationFaults = null; //reset
      if(record.Status == EntityStatus.Deleting)
        return true;
      record.EntityInfo.Events.OnValidatingChanges(record);
      foreach(var member in record.EntityInfo.Members) {
        switch(member.Kind) {
          case EntityMemberKind.Column:
            // We do not validate columns that are not updated.
            // We do not validate foreign keys - instead, we validate corresponding EntityRef members.
            if(member.Flags.IsSet(EntityMemberFlags.NoDbUpdate | EntityMemberFlags.NoDbInsert | EntityMemberFlags.ForeignKey))
              continue;
            ValidateValueMember(record, member);
            continue;
          case EntityMemberKind.EntityRef:
            ValidateEntityRefMember(record, member);
            continue;
          case EntityMemberKind.EntityList:
          case EntityMemberKind.Transient:
            continue; //do nothing
        }
      }//foreach member
      if(!record.IsValid) {
        record.EntityInfo.Events.OnValidationFailed(record);
      }
      return record.IsValid;
    }

    private void ValidateEntityRefMember(EntityRecord record, EntityMemberInfo member) {
      bool nullable = member.Flags.IsSet(EntityMemberFlags.Nullable);
      if(nullable)
        return; //nothing to check
      var isNull = record.KeyIsNull(member.ReferenceInfo.FromKey);
      if(isNull)
        record.AddValidationError(ClientFaultCodes.ValueMissing, ClientFaultMessages.ValueMissing, new object[] { member.MemberName }, 
          member.MemberName);
    }


    private void ValidateValueMember(EntityRecord record, EntityMemberInfo member) {
      bool nullable = member.Flags.IsSet(EntityMemberFlags.Nullable);
      var value = record.GetValue(member);
      // Checking non-nullable fields; note that identity field for new records is initialized with temp value (in ValueBox)
      if(!nullable) {
        if(member.DataType == typeof(string)) {
          // Treat empty string as null
          if(string.IsNullOrEmpty((string)value)) {
            record.AddValidationError(ClientFaultCodes.ValueMissing, ClientFaultMessages.ValueMissing, new object[] { member.MemberName },
              member.MemberName);
          }
        } else {
          if(value == null || value == DBNull.Value)
          record.AddValidationError(ClientFaultCodes.ValueMissing, ClientFaultMessages.ValueMissing, new object[] { member.MemberName }, 
            member.MemberName);
        }
      }
      //Check string size
      if(member.DataType == typeof(string) && member.Size > 0) {
        var strValue = (string)value;
        if(strValue != null && strValue.Length > member.Size && member.Flags.IsSet(EntityMemberFlags.AutoValue)) {
          strValue = strValue.Substring(member.Size);
          record.SetValueDirect(member, strValue);
        } else {
          if(value != null && strValue.Length > member.Size)
            record.AddValidationError(ClientFaultCodes.ValueTooLong, ClientFaultMessages.ValueTooLong, new object[] { member.MemberName, member.Size }, 
              member.MemberName, strValue);
        }
      }
    }
    #endregion

    #region Misc methods

    public void RegisterForClearLists(EntityRecord record) {
      // may need some concurrency safety (for m-threaded session)
      this.RecordsToClearLists.Add(record); 
    }

    public void ScheduleLinqNonQuery<TEntity>(IQueryable baseQuery, LinqCommandKind dbOperation,
                             CommandSchedule schedule = CommandSchedule.TransactionEnd) {
      Util.Check(baseQuery is EntityQuery, "query parameter should an EntityQuery.");
      var model = Context.App.Model;
      var targetEnt = model.GetEntityInfo(typeof(TEntity));
      Util.Check(targetEnt != null, "Generic parameter {0} is not an entity registered in the Model.", typeof(TEntity));
      var command = new LinqCommand(baseQuery.Expression, dbOperation, targetEnt, schedule);
      LinqCommandAnalyzer.Analyze(model, command);
      LinqExpressionHelper.EvaluateCommandParameters(command, this);
      this.ScheduledCommands.Add(command);
    }

    public void SetLastCommand(System.Data.IDbCommand command) {
      LastCommand = command;
    }

    // Used for unit testing, to see how validation errors propagate to client. 
    // We disable validation on the client, while keep it enabled  on the server
    bool _validationDisabled;
    public void EnableValidation(bool enable) {
      _validationDisabled = !enable;
    }

    private void CheckNotReadonly() {
      Util.Check(!IsReadOnly, "Cannot modify records, session is readonly.");
    }

    public void AddLogEntry(LogEntry entry) {
      if(this.LogEnabled)
        Log.AddEntry(entry);
    }
    #endregion

    #region TransactionTags
    // free-form strings associated with next save transaction; cleared after SaveChanges()
    StringSet _transationTags;
    public StringSet TransactionTags {
      get {
        _transationTags = _transationTags ?? new StringSet();
        return _transationTags;
      }
    }
    #endregion 

    //this method is defined for conveniences; only derived method in SecureSession does real elevation
    // But we want to be able to call this method without checking if we have secure session or not. 
    public virtual IDisposable ElevateRead() {
      return new DummyDisposable();
    }

    public virtual IDisposable WithNoCache() {
      return new CacheDisableToken(this);
    }

    #region nested Disposable tokens
    class CacheDisableToken : IDisposable {
      bool _wasEnabled; 
      EntitySession _session;

      public CacheDisableToken(EntitySession session) {
        _session = session; 
        _wasEnabled = session.CacheEnabled;
        if(_wasEnabled)
          session.EnableCache(false);
      }
      public void Dispose() {
        if(_wasEnabled)
          _session.EnableCache(true);
      }
    }
    class DummyDisposable : IDisposable {
      public void Dispose() { }
    }
    #endregion 

    public bool CacheEnabled {
      get { return !Options.IsSet(EntitySessionOptions.DisableCache); }
    }
    public bool LogEnabled{
      get { return this.Log != null && !Options.IsSet(EntitySessionOptions.DisableLog); }
    }

    public void SetOption(EntitySessionOptions option, bool on) {
      if(on)
        Options |= option;
      else
        Options &= ~option;
    }

  }//class

}//namespace
