﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Vita.Modules.JobExecution {

  [Flags]
  public enum JobModuleFlags {
    None = 0,
    AutoRestartJobsOnStart = 1, 
    TakeOverLongOverdueJobs = 1 << 1,

    Default = AutoRestartJobsOnStart | TakeOverLongOverdueJobs,
  }

  public class RetryPolicy {
    public readonly int[] RetryIntervals;
    public RetryPolicy(int[] retryIntervals) {
      RetryIntervals = retryIntervals; 
    }
    public override string ToString() {
      return AsString;
    }
    public string AsString {
      get { return string.Join(",", RetryIntervals); }
    }
    internal bool IsNoRetries() {
      return RetryIntervals == null || RetryIntervals.Length == 0;
    }

    public static RetryPolicy Default = new RetryPolicy(new[] { 1, 5, 30, 360, 720, 1440 });
    public static RetryPolicy NoRetries = new RetryPolicy(new int [] {});
  }

  public class JobModuleSettings {
    public string HostName;
    public JobModuleFlags Flags;  

    public JobModuleSettings(string hostName = null, JobModuleFlags flags = JobModuleFlags.Default, RetryPolicy defaultRetryPolicy = null) {
      HostName = hostName ?? System.Net.Dns.GetHostName();
      Flags = flags;
    }

    public RetryPolicy DefaultRetryPolicy {
      get {
        _retryPolicy = _retryPolicy ?? RetryPolicy.Default;
        return _retryPolicy;
      }
      set { _retryPolicy = value; }
    } RetryPolicy _retryPolicy; 
  }// class

}
