﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Diagnostics;

using Vita.Entities;
using Vita.Data;
using Vita.Data.Upgrades;

namespace Vita.Modules.Logging {
  using Vita.Data.Runtime;
  using Vita.Entities.Services;

  public class DbUpgradeLogModule : EntityModule, IDbUpgradeLogService  {
    public static readonly Version CurrentVersion = new Version("1.1.0.0");
    
    bool _connected;
    IList<DbUpgradeReport> _pendingBatches = new List<DbUpgradeReport>();
    object _lock = new object(); 

    public static Type[] EntityTypes = new Type[] { typeof(IDbUpgradeBatch), typeof(IDbUpgradeScript) };

    public DbUpgradeLogModule(EntityArea area, string name = "DbUpgradeLog") : base(area, name, version: CurrentVersion) {
      RegisterEntities(EntityTypes);
      App.RegisterService<IDbUpgradeLogService>(this);
    }


    public void LogDbUpgrade(DbUpgradeReport report) {
      lock(_lock) {
        _pendingBatches.Add(report);
      }
      Flush(); 
    }

    public void Flush() {
      if(!_connected)
        return;
      lock(_lock) {
        if(_pendingBatches.Count == 0)
          return; 
        var session = App.OpenSystemSession();
        session.EnableLog(false);
        int index = 0;
        foreach(var batch in _pendingBatches) {
          var iBatch = session.NewDbModelChangeBatch(batch.OldDbVersion.ToString(), batch.Version.ToString(), batch.StartedOn, batch.CompletedOn,
                                                     batch.Method, batch.MachineName, batch.UserName, batch.Exception);
          foreach(var scr in batch.Scripts) {
            iBatch.NewDbModelChangeScript(scr, index++);
          }
          if(batch.Exception != null && batch.FailedScript != null) {
            iBatch.NewDbModelChangeScript(batch.FailedScript, 0, batch.Exception);
          }
        }//foreach
        session.SaveChanges();
        _pendingBatches.Clear(); 
      }//lock
    }

  }//class
}
