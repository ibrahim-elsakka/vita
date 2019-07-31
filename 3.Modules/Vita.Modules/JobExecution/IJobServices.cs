﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Text;
using System.Threading.Tasks;
using Vita.Entities;

namespace Vita.Modules.JobExecution {

  /// <summary>The service allows you to start and reliably execute sync or async tasks, with several retries in case of error. </summary>
  /// <remarks>
  /// You can define long-running processes, with progress reporting, or short asynchonous tasks with repeates after short/long periods of time. 
  /// The parameters of the method are deserialized on start from the database, and can be saved (serialized) when method completes with success or error. 
  /// </remarks>
  public interface IJobInformationService {

    /// <summary>Returns a list of currently running jobs. </summary>
    /// <returns>A list of running context objects for all jobs currently executing.</returns>
    /// <remarks>Includes only jobs that are currently in memory and executing. Does not include jobs that failed and waiting for retries.</remarks>
    IList<JobRunContext> GetRunningJobs();

    /// <summary>A notification event, fired when jobs are started, completed or failed. </summary>
    event EventHandler<JobNotificationEventArgs> Notify;


  }

  /// <summary>Job notification event arguments. </summary>
  public class JobNotificationEventArgs : EventArgs {
    public JobRunContext JobRunContext;
    public JobNotificationType NotificationType;
    public Exception Exception;
  }

  /// <summary>Job execution service, provides methods to create and execute jobs.</summary>
  public interface IJobExecutionService {
    Task<JobRunContext> ExecuteWithRetriesAsync(OperationContext context, string jobName, Expression<Func<JobRunContext, Task>> func, RetryPolicy retryPolicy = null);
    JobRunContext ExecuteWithRetriesNoWait(OperationContext context, string jobName, Expression<Action<JobRunContext>> action, RetryPolicy retryPolicy = null);

    IJob CreateJob(IEntitySession session, string name, LambdaExpression jobMethod,
           JobThreadType threadType = JobThreadType.Pool, RetryPolicy retryPolicy = null);
    IJobRun StartJobOnSaveChanges(IJob job, Guid? dataId = null, string data = null);
    IJobRun ScheduleJobRunOn(IJob job, DateTime runOnUtc, Guid? dataId = null, string data = null, string hostName = null);
    IJobSchedule CreateJobSchedule(IJob job, string name, string cronSpec, DateTime? activeFrom, DateTime? activeUntil, string hostName = null);

    int StartInterruptedJobsAfterAfterRestart(OperationContext context);
  }

  /// <summary>Diagnostics service, primary use is in testing. </summary>
  public interface IJobDiagnosticsService {
    /// <summary>Waits for all currently starting jobs to actually start. 
    /// Use it to wait for all job runs due to start to actually start after timer event had been fired. </summary>
    void WaitStarting();

    /// <summary>Waits until all running jobs are completed. </summary>
    void WaitAllCompleted();
  }


}
