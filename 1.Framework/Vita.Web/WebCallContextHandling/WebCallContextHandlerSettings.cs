﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using Vita.Entities;
using Vita.Entities.Services;
using Vita.Entities.Utilities;

namespace Vita.Web {

  [Flags]
  public enum WebHandlerOptions {
    None = 0,
    // For Debug mode only - return exception details for fatal exceptions
    ReturnExceptionDetails = 1,
    ReturnBadRequestOnAuthenticationRequired = 1 << 1,

    DefaultDebug = ReturnExceptionDetails | ReturnBadRequestOnAuthenticationRequired,
    DefaultProduction = ReturnBadRequestOnAuthenticationRequired,
  }

  public enum LogLevel_TEMP {
    Basic
  }

  public class WebCallContextHandlerSettings {
    public const string DefaultVersionToken = "X-Versions";

    public LogLevel_TEMP LogLevel;
    public WebHandlerOptions Options = WebHandlerOptions.DefaultDebug;
    public StringSet IgnorePaths = new StringSet();
    public StringSet FilterPaths = new StringSet(); 
    public IList<string> NoLogHeaders = new List<string>(new[] { "Auhorization" });
    public DbConnectionReuseMode ConnectionReuseMode;
    public IList<WebTokenHandler> TokenHandlers = new List<WebTokenHandler>(); 
    //constructor
    public WebCallContextHandlerSettings(LogLevel_TEMP logLevel = LogLevel_TEMP.Basic,
                        WebHandlerOptions options = WebHandlerOptions.DefaultDebug, 
                        string sessionToken = "Authorization", WebTokenType sessionTokenType = WebTokenType.Header,
                        string versionToken = DefaultVersionToken, string csrfToken = null,   
                        DbConnectionReuseMode connectionReuseMode = DbConnectionReuseMode.KeepOpen ) {
      LogLevel = logLevel;
      Options = options;
      ConnectionReuseMode = connectionReuseMode;
      if (sessionToken != null)
        TokenHandlers.Add(new WebSessionTokenHandler(sessionToken, sessionTokenType));
      if (versionToken != null)
        TokenHandlers.Add(new VersionTokenHandler(versionToken));
      // Cross-Site Request Forgery (CSRF) protection. Used as header only (not cookie), when session token is saved in cookie, 
      // to protect against CSRF execution. Sometimes called synchronization token; read more in Wikipedia or other resources
      if (csrfToken != null)
        TokenHandlers.Add(new WebTokenHandler(csrfToken, WebTokenType.Header, WebTokenDirection.InputOutput));
      //We ignore Swagger paths by default
      IgnorePaths.Add("/swagger");
    }

  }//class
}
