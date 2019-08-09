﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using Vita.Entities.Services;

namespace Vita.Entities.Logging {

  // not used yet, will be used in Web apps

  /// <summary>BufferedOperationLog accumulates entries internally.  </summary>
  public class BufferedLog : IBufferingLog {
    public int MaxEntries = 1000;
    ILogService _logService;
    LogContext _logContext; 
    ConcurrentQueue<LogEntry> _entries = new ConcurrentQueue<LogEntry>();
    private int _errorCount;
    // ConcurrentQueue.Count is expensive property (see sources), so we track total count in our own field 'approximate count'.
    //  https://referencesource.microsoft.com/#mscorlib/system/Collections/Concurrent/ConcurrentQueue.cs
    int _approxCount;

    public BufferedLog(LogContext context, int maxEntries = 1000, ILogService logService = null) {
      _logContext = context;
      MaxEntries = maxEntries;
      _logService = logService; 
    }

    public void AddEntry(LogEntry entry) {
      _entries.Enqueue(entry);
      var count = Interlocked.Increment(ref _approxCount);
      if (entry.EntryType == LogEntryType.Error)
        Interlocked.Increment(ref _errorCount);

      if (count > MaxEntries) {
        if(_logService != null)
          Flush();
        else {
          // simply remove two oldest entries
          _entries.TryDequeue(out LogEntry dummy);
          _entries.TryDequeue(out dummy);
          Interlocked.Add(ref _approxCount, -2);
        }
      }
    }

    public void Flush() {
      if(_logService == null)
        return; 
      var entries = DequeueAll();
      if(entries.Count == 0)
        return;
      var compEntry = new BatchedLogEntry(_logContext, entries);              
      _logService.AddEntry(compEntry);
    }

    public int ErrorCount => _errorCount;

    public IList<LogEntry> GetAll() => _entries.ToList(); 

    private IList<LogEntry> DequeueAll() {
      var entries = new List<LogEntry>();
      while(_entries.TryDequeue(out LogEntry entry))
        entries.Add(entry);
      Interlocked.Exchange(ref _approxCount, 0);
      return entries; 
    }

    public IList<LogEntry> GetAllEntries(bool clear = true) {
      var arr = _entries.ToArray();
      if(clear) {
        Interlocked.Exchange(ref _approxCount, 0); 
        _entries = new ConcurrentQueue<LogEntry>();
      }
      return arr;  
    }

  } //class

}