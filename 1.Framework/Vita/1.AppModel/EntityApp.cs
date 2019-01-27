﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Reflection;

using Vita.Entities.Api;
using Vita.Entities.Model;
using Vita.Entities.Services;
using Vita.Entities.Logging;
using Vita.Entities.Services.Implementations;
using Vita.Data;
using System.Diagnostics;
using Vita.Data.Model;
using Vita.Entities.Model.Construction;
using Vita.Entities.Utilities;
using Vita.Data.Runtime;
using System.Threading.Tasks;
using System.Threading;

namespace Vita.Entities {

  /// <summary> Represents entity application status, from initialization to shutdown. </summary>
  public enum EntityAppStatus {
    Created,
    /// <summary> Application is initializing. </summary>
    Initializing,
    Initialized,
    Connected,
    Shutdown,
  }


  /// <summary>Entity application. </summary>
  public partial class EntityApp {

    /// <summary>The app name. Defaults to type name. </summary>
    public string AppName;

    /// <summary>Entity application status, from initialization to shutdown. </summary>
    public EntityAppStatus Status { get; protected set; }

    ///<summary>Entity application version, formatted as '1.0.0.0' . </summary>
    public Version Version;

    /// <summary>Gets a collection of registered areas. EntityArea is a representation of database schema object like 'dbo'. </summary>
    public IEnumerable<EntityArea> Areas {
      get { return _areas; }
    } IList<EntityArea> _areas = new List<EntityArea>();

    /// <summary>Gets a list of entity modules in the application. </summary>
    public IEnumerable<EntityModule> Modules {
      get { return _modules; }
    } IList<EntityModule> _modules = new List<EntityModule>();

    /// <summary>Application-level events.</summary>
    public readonly EntityAppEvents AppEvents;
    /// <summary>Data events.</summary>
    public readonly DataSourceEvents DataSourceEvents;

    /// <summary>Default length for string properties without Size attribute. </summary>
    public int DefaultStringLength = 50;

    /// <summary>Entity class provider. The service responsible for generating (emitting) entity classes. </summary>
    /// <remarks>The default implementation based on IL-emit is defined in a separate assembly/package Vita.Entities.Emit. 
    /// This assembly is not referenced by Vita.Entities so it cannot be assigned automatically. 
    /// The hosting environment (the code that sets up EntityApp and connects it to the database)
    /// must reference the Vita.Entities.Emit package, and make the call the EntityApp.ConfigureEmitClassProvider extension method.
    /// The separation of class emitter is done in order to allow VITA to be used under environments like iOS which does not allow code generation 
    /// at runtime and does not have IL-emit implementations. Under iOS you can use a different provider that uses entity classes generated as c# source.
    /// These entity classes are generated by a separate tool. 
    /// </remarks>
    public IEntityClassProvider EntityClassProvider;

    public ApiConfiguration ApiConfiguration = new ApiConfiguration();

    public Sizes.SizeTable SizeTable = Sizes.GetDefaultSizes();

    /// <summary>Gets or sets a path for the activation log file. </summary>
    /// <remarks>System log contains messages/errors regarding app startup/connect/shutdown events. </remarks>
    public string ActivationLogPath {
      get { return ActivationLog.FileName; }
      set { ActivationLog.FileName = value; }
    } 

    public IActivationLog ActivationLog;

    /// <summary>Gets or sets operation log file name/path.</summary>
    /// <remarks>Operation log contains SQLs of regular operations, and messages logged by
    ///  the application code. </remarks>
    public string LogPath;
    public string ErrorLogPath;

    // created automatically if LogPath is specified
    public ILogListener LogFileWriter;
    public ILogListener ErrorLogFileWriter;

    /// <summary>Gets the instance of the application time service. </summary>
    public readonly ITimeService TimeService;

    /// <summary>Linked applications. Linked applications provide external services to the main app. </summary>
    public readonly IList<EntityApp> LinkedApps = new List<EntityApp>();

    /// <summary>Data access service. </summary>
    public readonly IDataAccessService DataAccess;
    /// <summary>Entity model. Initially null, available after the app is initialized. </summary>
    public EntityModel Model { get; internal set; }

    private IDictionary<Type, object> _services;
    private object _lock = new object();

    /// <summary> Constructs a new EntityApp instance. </summary>
    public EntityApp(string appName = null, string version = "1.0.0.0", string activationLogPath = null) {
      _services = new Dictionary<Type, object>();
      _shutdownTokenSource = new CancellationTokenSource();
      AppName = appName ?? this.GetType().Name;
      Version = new Version(version);
      Status = EntityAppStatus.Created;
      AppEvents = new EntityAppEvents(this);
      DataSourceEvents = new DataSourceEvents(this); 
      AppDomain.CurrentDomain.DomainUnload += CurrentDomain_DomainUnload;
      // Time service and Timers service  are global singletons, we register these here, as early as possible
      this.TimeService = this.RegisterService<ITimeService>(Vita.Entities.Services.Implementations.TimeService.Instance);
      var timers = this.RegisterService<ITimerService>(new TimerService());
      this.RegisterService<ITimerServiceControl>(timers as ITimerServiceControl);
      this.RegisterService<ILogService>(new DefaultLogService());
      var custService = new EntityModelCustomizationService(this);
      this.RegisterService<IEntityModelCustomizationService>(custService);
      this.DataAccess = RegisterService<IDataAccessService>(new DataAccessService(this));
      RegisterService<IBackgroundTaskService>(new DefaultBackgroundTaskService());
      RegisterService<IHashingService>(new HashingService());
      ActivationLog = new ActivationLog(activationLogPath, app: this);
    }

    private void CurrentDomain_DomainUnload(object sender, EventArgs e) {
      Shutdown();
    }

    /// <summary>Returns entity app name. </summary>
    /// <returns></returns>
    public override string ToString() {
      return AppName;
    }

    /// <summary>Adds an area (logical equivalent of database schema like 'dbo') to the data model.</summary>
    /// <param name="areaName">Area name. It is default for schema name, unless it is mapped to different schema.</param>
    /// <returns>A new area instance.</returns>
    /// <remarks>Before you can create modules and register entities for your entity app, 
    /// you must create at least one area. </remarks>
    public EntityArea AddArea(string areaName) {
      var area = new EntityArea(this, areaName);
      _areas.Add(area);
      return area;
    }

    /// <summary>Adds an entity module to the application. </summary>
    /// <param name="module">A module to add.</param>
    internal void AddModule(EntityModule module) {
      if(!_modules.Contains(module)) //prevent duplicates
        _modules.Add(module);
    }

    public TModule GetModule<TModule>() where TModule : EntityModule {
      var result = Modules.FirstOrDefault(m => m is TModule) as TModule;
      if(result != null)
        return result;
      foreach(var linkedApp in LinkedApps) {
        result = linkedApp.GetModule<TModule>();
        if(result != null)
          return result;
      }
      return null;
    }

    public bool IsConnected() {
      return Status == EntityAppStatus.Connected || Status == EntityAppStatus.Shutdown;
    }


    #region Services
    /// <summary> Gets a service by service type. </summary>
    /// <typeparam name="TService">Service type, usually an interface type.</typeparam>
    /// <param name="throwIfNotFound">Throw exception if service is not found.</param>
    /// <returns>Service implementation.</returns>
    public TService GetService<TService>(bool throwIfNotFound = true) where TService : class {
      var serv = (TService)this.GetService(typeof(TService));
      if(serv == null && throwIfNotFound)
        Util.Check(false, "Service {0} not registered.", typeof(TService));
      return serv;
    }
    /// <summary> Gets a service by service type. </summary>
    /// <param name="serviceType">Service type.</param>
    /// <returns></returns>
    public object GetService(Type serviceType) {
      object result;
      if(_services.TryGetValue(serviceType, out result))
        return result;
      foreach(var linkedApp in LinkedApps) {
        result = linkedApp.GetService(serviceType);
        if(result != null)
          return result;
      }
      return null;
    }

    /// <summary>Registers a service with an application. </summary>
    /// <typeparam name="T">Service type used as a key in internal servcies dictionary. Usually it is an interface type.</typeparam>
    /// <param name="service">Service implementation.</param>
    /// <returns>Service instance.</returns>
    /// <remarks>The most common use for the services is entity module registering itself as a service for the application.
    /// For example, ErrorLogModule registers IErrorLogService that application code and other modules can use to log errors.
    /// </remarks>
    public T RegisterService<T>(T service) {
      _services[typeof(T)] = service;
      this.AppEvents.OnServiceAdded(this, typeof(T), service);
      //notify child apps
      foreach(var linkedApp in this.LinkedApps)
        linkedApp.AppEvents.OnServiceAdded(this, typeof(T), service);
      return service;
    }

    public void RemoveService(Type serviceType) {
      if(_services.ContainsKey(serviceType))
        _services.Remove(serviceType);
    }
    /// <summary>Returns the list of all service types (keys) registered in the application. </summary>
    /// <returns>List of service interface types.</returns>
    public IList<Type> GetAllServiceTypes() {
      return _services.Keys.ToList();
    }
    private IList<object> GetAllServices() {
      return _services.Values.ToList();
    }

    /// <summary>Imports services from external service provider. </summary>
    /// <param name="app">External service provider, usually another entity applications.</param>
    /// <param name="serviceTypes">Types of services to import.</param>
    public void ImportServices(EntityApp app, params Type[] serviceTypes) {
      foreach(var type in serviceTypes) {
        var serv = app.GetService(type);
        if(serv != null)
          this._services[type] = app.GetService(type);
      }
    }

    #endregion

    #region ServiceFunc dictionary
    // ServiceFuncs are similar to services, but allow connecting consumer to service without using shared interfaces. 
    // The service Func is simply Func<..> or Action<> with a number of parameters. It is added by a service, 
    // and retrieved by another module. 
    // For example, LoginModule (defined in Vita.Modules.Login) can use LoginLogModule (in Vita.Modules.Logging)
    //  without requiring shared interface (ILoginLog). As a result, Vita.Modules.Login assembly does not need 
    // to reference Vita.Modules.Logging assembly. 
    Dictionary<string, object> _serviceFuncs = new Dictionary<string, object>(); 
    public TFunc GetServiceFunc<TFunc>(string name) where TFunc : class {
      object result = GetServiceFunc(name);
      if (result != null) { //check matching type
        var func = result as TFunc;
        Util.Check(func != null, "GetServiceFunc: Func with name {0} found but delegate type does not match. " +
          "Requested type: {1}, found type: {2}", name, typeof(TFunc), result.GetType());
        return func; 
      }
      return null; 
    }
    public object GetServiceFunc(string name) {
      if(_serviceFuncs.TryGetValue(name, out object result))
        return result;
      return null; 
    }
    public void RegisterServiceFun(string name, object func) {
      _serviceFuncs[name] = func; 
    }
    #endregion 

    /// <summary>Returns a list of all entity types from all entity modules. </summary>
    /// <returns></returns>
    public IEnumerable<Type> GetAllEntityTypes() {
      return this.Modules.SelectMany(m => m.Entities);
    }

    /// <summary>Fires an event requesting all logging facilities to flush buffers. </summary>
    public void Flush() {
      ActivationLog.Flush(); 
      foreach(var linkedApp in LinkedApps)
        linkedApp.Flush();
      AppEvents.OnFlushRequested();
    }

    #region Configs dictionary

    //Repository of all config/settings objects, indexed by type, for easy access from anywhere
    Dictionary<Type, object> _configsRepo = new Dictionary<Type, object>();

    /// <summary>Registers config/settings object in global repo.</summary>
    /// <typeparam name="T">Type of config object.</typeparam>
    /// <param name="config">Config object.</param>   
    public void RegisterConfig<T>(T config) where T : class {
      lock(_lock) {
        _configsRepo[typeof(T)] = config;
      }
    }

    /// <summary>Retrieves config object based on type. </summary>
    /// <typeparam name="T">Config object type.</typeparam>
    /// <returns>Config object.</returns>
    public T GetConfig<T>(bool throwIfNotFound = true) where T : class {
      lock(_lock) {
        object config;
        if(_configsRepo.TryGetValue(typeof(T), out config))
          return (T)config;
      }
      if(throwIfNotFound)
        Util.Throw("Config/settings object of type {0} is not registered in ConfigRepo. " +
                   "Possibly owner module is not included into the app.", typeof(T));
      return null; //never happens
    }

    #endregion

    #region Methods to override (optionally)
    public virtual string GetUserDispalyName(UserInfo user) {
      return user.UserName;
    }
    #endregion


    #region Init, connect, shutdown

    public void Init() {
      if(Status != EntityAppStatus.Created)
        return;
      InitApp(); 
    }
    /// <summary>Creates a data source (database) using provided DB setttings and registers it with data access service.</summary>
    /// <param name="dbSettings">Database settings.</param>
    public void ConnectTo(DbSettings dbSettings) {
      ConnectToDatabase(dbSettings); 
    }

    public CancellationToken ShutdownToken {
      get { return _shutdownTokenSource.Token; }
    }
    CancellationTokenSource _shutdownTokenSource;

    /// <summary>Performs the shutdown of the application. Notifies all components and modules about pending application shutdown. 
    /// </summary>
    public virtual void Shutdown() {
      ActivationLog.Info("Shutting down app {0}. =======================\r\n\r\n", AppName);
      Flush();

      if (_shutdownTokenSource != null) {
        _shutdownTokenSource.Cancel();
        Task.Yield(); 
      }

      Status = EntityAppStatus.Shutdown;
      foreach(var module in this.Modules)
        module.Shutdown();
      //shutdown services
      var servList = this.GetAllServices();
      for(int i = 0; i < servList.Count; i++) {
        var service = servList[i];
        var iEntService = service as IEntityServiceBase;
        if(iEntService != null)
          iEntService.Shutdown();
      }
      if(this.LogFileWriter != null) {
      }
      AppEvents.OnShutdown();
    }

    #endregion 
  }//class

}//ns