﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Vita.Entities; 

namespace Vita.Modules.DataHistory {

  public class DataHistoryEntry {
    public Guid EntryId;
    public DateTime CreatedOn;
    public Guid CreatedByUserId;
    public Guid? TransactionId;
    public HistoryAction Action;
    public Type EntityType;
    public string PrimaryKeyValue; 
    public IDictionary<string, object> Values = new Dictionary<string, object>();
  }

  public interface IDataHistoryService {
    void RegisterToKeepHistoryData(params Type[] entityTypes);
    ICollection<Type> GetTrackedEntities();
    IList<DataHistoryEntry> GetEntityHistory(IEntitySession session, Type entityType, object primaryKey, 
                    DateTime? fromDate = null, DateTime? tillDate = null, int skip = 0, int take = 100, Guid? userId = null);
    DataHistoryEntry GetEntityOnDate(IEntitySession session, Type entityType, object primaryKey, DateTime onDate);
  }
}
