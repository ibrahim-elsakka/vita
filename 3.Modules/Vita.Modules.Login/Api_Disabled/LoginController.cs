﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net;

using Vita.Entities;
using Vita.Entities.Services; 
using Vita.Entities.Api;

namespace Vita.Modules.Login.Api {

  // Handles Login/logout operations
  [ApiRoutePrefix("login"), ApiGroup("Login")]
  public class LoginController : SlimApiController {
    ILoginService _loginService;
    ILoginProcessService _processService;

    public override void InitController(OperationContext context) {
      base.InitController(context);
      _loginService = Context.App.GetService<ILoginService>();
      _processService = Context.App.GetService<ILoginProcessService>();
    }

    /// <summary>Performs user login with user name and password.</summary>
    /// <param name="request">The request object.</param>
    /// <returns>An object containing login attempt result.</returns>
    [ApiPost, ApiRoute("")]
    public LoginResponse Login(LoginRequest request) {
      Context.ThrowIfNull(request, ClientFaultCodes.ContentMissing, "LoginRequest", "Content object missing in API request.");
      Context.WebContext.Flags |= WebCallFlags.Confidential;
      //Login using LoginService
      var loginResult = _loginService.Login(this.Context, request.UserName, request.Password, request.TenantId, request.DeviceToken);
      var login = loginResult.Login;
      switch(loginResult.Status) {
        case LoginAttemptStatus.PendingMultifactor:
          var processService = Context.App.GetService<ILoginProcessService>();
          var token = processService.GenerateProcessToken();
          var process = processService.StartProcess(loginResult.Login, LoginProcessType.MultiFactorLogin, token);
          return new LoginResponse() { Status = LoginAttemptStatus.PendingMultifactor, MultiFactorProcessToken = token };
        case LoginAttemptStatus.AccountInactive:
          // return AccountInactive only if login allows to disclose membership
          var reportStatus = login.Flags.IsSet(LoginFlags.DoNotConcealMembership) ? LoginAttemptStatus.AccountInactive : LoginAttemptStatus.Failed;
          return new LoginResponse() { Status = reportStatus };
        case LoginAttemptStatus.Failed:
        default:
          return new LoginResponse() { Status = loginResult.Status };
        case LoginAttemptStatus.Success:
          var displayName = Context.App.GetUserDispalyName(loginResult.User);
          return new LoginResponse() {
            Status = LoginAttemptStatus.Success,
            UserName = login.UserName, UserDisplayName = displayName,
            UserId = login.UserId, AltUserId = login.AltUserId, LoginId = login.Id,
            PasswordExpiresDays = login.GetExpiresDays(), Actions = loginResult.Actions, LastLoggedInOn = loginResult.LastLoggedInOn,
            SessionId = loginResult.SessionId
          };
      }//switch
    }

    /// <summary>Logs out the user. </summary>
    [ApiDelete, ApiRoute("")]
    public void Logout() {
      if(Context.User.Kind != UserKind.AuthenticatedUser)
        return; 
      _loginService.Logout(this.Context);
    } // method

    //Duplicate of method in PasswordResetController - that's ok I think
    /// <summary>Returns login process identified by a token. </summary>
    /// <param name="token">Token identifying the process.</param>
    /// <returns>Login process object.</returns>
    /// <remarks>Login process is a server-side persistent object that tracks user progress through multi-step operations like password reset, multi-factor login, etc.</remarks>
    [ApiGet, ApiRoute("multifactor/process")]
    public LoginProcess GetMultiFactorProcess(string token) {
      var session = Context.OpenSession(); 
      var process = GetMutiFactorProcess(session, token, throwIfNotFound: false);
      return process.ToModel();
    }

    /// <summary>Requests the server to generate and send a pin to user (by email or SMS). </summary>
    /// <param name="pinRequest">Pin request information. 
    /// Should contain process token and factor type (email, phone).</param>
    [ApiPost, ApiRoute("multifactor/pin/send")]
    public async Task SendPinForMultiFactor(SendPinRequest pinRequest) {
      var session = Context.OpenSession();
      var process = GetMutiFactorProcess(session, pinRequest.ProcessToken);
      Context.ThrowIf(process.CurrentFactor != null, ClientFaultCodes.InvalidAction, "token", "Factor verification pending, the previous process step is not completed.");
      var pendingFactorTypes = process.PendingFactors;
      Context.ThrowIf(!pendingFactorTypes.IsSet(pinRequest.FactorType), ClientFaultCodes.InvalidValue, "factortype", "Factor type is not pending in login process");
      var factor = process.Login.ExtraFactors.FirstOrDefault(f => f.FactorType == pinRequest.FactorType); 
      Context.ThrowIfNull(factor, ClientFaultCodes.ObjectNotFound, "factor", 
        "Login factor (email or phone) not setup in user account; factor type: {0}", pinRequest.FactorType);
      await _processService.SendPinAsync(process, factor); 
    }

    /// <summary>Asks the server to verify the pin entered by the user or extracted from URL in email. </summary>
    /// <param name="request">The request object containing process token and pin value.</param>
    /// <returns>True if the pin is verified and is correct; otherwise, false.</returns>
    [ApiPut, ApiRoute("multifactor/pin/verify")]
    public bool VerifyPinForMultiFactor(VerifyPinRequest request) {
      var session = Context.OpenSession();
      var process = GetMutiFactorProcess(session, request.ProcessToken);
      Context.ThrowIfNull(process, ClientFaultCodes.ObjectNotFound, "Process", "Process not found or expired.");
      Context.ThrowIfEmpty(request.Pin, ClientFaultCodes.ValueMissing, "Pin", "Pin value missing.");
      var result = _processService.SubmitPin(process, request.Pin);
      session.SaveChanges();
      return result; 
    }

    /// <summary>Requests the server to complete the multi-factor login process and to actually login the user. </summary>
    /// <param name="request">The request data: process token and expiration type.</param>
    /// <returns>User login information.</returns>
    /// <remarks>The process must be completed, all factors specified should be verified by this time, 
    /// so PendingFactors property is empty.</remarks>
    [ApiPost, ApiRoute("multifactor/complete")]
    public LoginResponse CompleteMultiFactorLogin(MultifactorLoginRequest request) {
      var session = Context.OpenSession();
      var process = GetMutiFactorProcess(session, request.ProcessToken);
      Context.ThrowIfNull(process, ClientFaultCodes.ObjectNotFound, "processToken", "Login process not found or expired.");
      Context.ThrowIf(process.PendingFactors != ExtraFactorTypes.None, ClientFaultCodes.InvalidValue, "PendingFactors",
        "Multi-factor login process not completed, verification pending: {0}.", process.PendingFactors);
      var login = process.Login;
      var loginResult = _loginService.CompleteMultiFactorLogin(Context, login);
      var displayName = Context.App.GetUserDispalyName(Context.User);
      session.SaveChanges(); 
      return new LoginResponse() {
        Status = LoginAttemptStatus.Success, SessionId = loginResult.SessionId,
        UserName = login.UserName, UserDisplayName = displayName,
        UserId = login.UserId, AltUserId = login.AltUserId, LoginId = login.Id,
        PasswordExpiresDays = login.GetExpiresDays(), Actions = loginResult.Actions, LastLoggedInOn = loginResult.LastLoggedInOn
      };
    }//method

    /// <summary>Submits the PIN received by user (in email) to verify email. </summary>
    /// <param name="processToken">Verification process token.</param>
    /// <param name="pin">The PIN value.</param>
    /// <returns>True if PIN value matches; otherwise, false.</returns>
    /// <remarks>This method does not require logged-in user. Use it when from a page activated 
    /// by URL embedded in verification email. 
    /// The other end point for pin verification (api/mylogin/factors/pin)
    /// requires logged in user, so it should be used only when user enters the pin manually on the page.
    /// </remarks>
    [ApiPut, ApiRoute("factors/verify-pin")]
    public bool VerifyEmailPin(string processToken, string pin) {
      Context.ThrowIfEmpty(processToken, ClientFaultCodes.ValueMissing, "processToken", "ProcessToken value missing");
      Context.ThrowIfEmpty(pin, ClientFaultCodes.ValueMissing, "pin", "Pin value missing");
      var session = Context.OpenSession();
      var process = _processService.GetActiveProcess(session, LoginProcessType.FactorVerification, processToken);
      Context.ThrowIfNull(process, ClientFaultCodes.ObjectNotFound, "processToken", "Login process not found or expired.");
      var processService = Context.App.GetService<ILoginProcessService>();
      var result = processService.SubmitPin(process, pin);
      session.SaveChanges();
      return result; 
    }

    /// <summary>Returns the list of user&quot;s secret questions. </summary>
    /// <param name="token">The token of login process.</param>
    /// <returns>The list of secret question objects for the user.</returns>
    [ApiGet, ApiRoute("multifactor/userquestions")]
    public IList<SecretQuestion> GetUserSecretQuestions(string token) {
      var session = Context.OpenSession();
      var process = GetMutiFactorProcess(session, token);
      var qs = _processService.GetUserSecretQuestions(process.Login);
      return qs.Select(q => q.ToModel()).ToList();
    }

    /// <summary>Verifies the answers to the secret questions by comparing them to original answers stored in the user account. </summary>
    /// <param name="token">Login process token.</param>
    /// <param name="answers">The answers.</param>
    /// <returns>True if the answers are correct; otherwise, false.</returns>
    [ApiPut, ApiRoute("multifactor/questionanswers")]
    public bool SubmitQuestionAnswers(string token, List<SecretQuestionAnswer> answers) {
      Context.WebContext.MarkConfidential();
      var session = Context.OpenSession();
      var process = GetMutiFactorProcess(session, token);
      var result = _processService.CheckAllSecretQuestionAnswers(process, answers);
      return result;
    }

    /// <summary>Verifies a single answer to the secret question. </summary>
    /// <param name="token">Login process token.</param>
    /// <param name="answer">The answer.</param>
    /// <returns>True if the answer is correct; otherwise, false.</returns>
    [ApiPut, ApiRoute("multifactor/questionanswer")]
    public bool SubmitQuestionAnswer(string token, SecretQuestionAnswer answer) {
      Context.WebContext.MarkConfidential();
      var session = Context.OpenSession();
      var process = GetMutiFactorProcess(session, token);
      var storedAnswer = process.Login.SecretQuestionAnswers.FirstOrDefault(a => a.Question.Id == answer.QuestionId);
      Context.ThrowIfNull(storedAnswer, ClientFaultCodes.InvalidValue, "questionId", "Question is not registered user question.");
      var success = _processService.CheckSecretQuestionAnswer(process, storedAnswer.Question, answer.Answer);
      return success;
    }

    /* Notes: 
     * 1. GET verb would be more appropriate, but then password will appear in URL (GET does not allow body), 
         and we want to avoid this, so it does not appear in web log. With PUT we set HasSensitiveData flag and 
         this prevents web log from logging request body
       2. This controller is not a logical place to host this method, password checking will be used in UI for 
          self-service password change or password reset process. To avoid implementing it in several places,
          and considering that password check is light-weight (no db access) and does not need to be secured, 
          we place it here. 
     */
    /// <summary>Asks server to evaluate the strength of a password. </summary>
    /// <param name="changeInfo">The password change information containing the password.</param>
    /// <returns>The strength of the password.</returns>
    [ApiPut, ApiRoute("passwordcheck")]
    public PasswordStrength EvaluatePasswordStrength(PasswordChangeInfo changeInfo) {
      Context.WebContext.MarkConfidential(); //prevent from logging password
      Context.ThrowIfNull(changeInfo, ClientFaultCodes.ContentMissing, "Password", "Password infomation missing.");
      var loginMgr =  Context.App.GetService<ILoginManagementService>();
      var strength = loginMgr.EvaluatePasswordStrength(changeInfo.NewPassword);
      return strength; 
    }

    private ILoginProcess GetMutiFactorProcess(IEntitySession session, string token, bool throwIfNotFound = true) {
      Context.ThrowIfEmpty(token, ClientFaultCodes.ValueMissing, "token", "Process token may not be empty.");
      var process = _processService.GetActiveProcess(session, LoginProcessType.MultiFactorLogin, token);
      if(throwIfNotFound)
        Context.ThrowIfNull(process, ClientFaultCodes.ObjectNotFound, "LoginProcess", "Invalid login process token - process not found or expired.");
      return process; 
    }


  }

}
