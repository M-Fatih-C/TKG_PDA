// TKG RDS integration — paste-ready installation template
// Generated for the current TKG PDA integration contract.
// IMPORTANT: replace ALL <...> placeholders before saving in RDS Script UI.
// Do not commit real secrets into Git.
//
// This script is still useful even when transport execution uses
// RDSCORE_NATIVE_ORDER:
//   - controlled WorkSite content updates use /script-api/tkg/update-content
//   - task/worksite callbacks provide low-latency evidence
//   - WIND_TASK mode remains supported as an explicit fallback/template mode
//
// callbackUrl must be reachable FROM the RDS server.
// Use 127.0.0.1 only if the TKG API really runs on the same host.

var TKG_RDS_SCRIPT_VERSION = "2026.10.07";

var TKG_RDS_INTEGRATION_CONFIG = {
  integrationKey: "<SAME VALUE AS BACKEND Rds__IntegrationKey>",
  callbackUrl: "http://<TKG_API_HOST>:<TKG_API_PORT>/api/integrations/rds/events",
  callbackKey: "<SAME VALUE AS BACKEND Rds__CallbackKey>"
};

(function (root, factory) {
  "use strict";
  var api = factory();
  if (typeof module === "object" && module.exports) module.exports = api;
  root.TkgRdsIntegration = api;
})(typeof globalThis !== "undefined" ? globalThis : this, function () {
  "use strict";

  function install(jj, config) {
    requireFunction(jj, "registerHandler");
    requireFunction(jj, "registerTaskEventFunction");
    requireFunction(jj, "isTaskLabelExist");
    requireFunction(jj, "isTaskRecordIdExist");
    requireFunction(jj, "getTaskRecordById");
    requireFunction(jj, "createWindTask");
    requireFunction(jj, "requestHttpPost");
    requireFunction(jj, "updateSitesByCondition");
    validateConfig(config);

    registerPost(jj, "/script-api/tkg/validate-transport", "tkgValidateTransport");
    registerPost(jj, "/script-api/tkg/create-transport", "tkgCreateTransport");
    registerPost(jj, "/script-api/tkg/update-content", "tkgUpdateContent");
    registerCallback(jj, "taskDone");
    registerCallback(jj, "taskFailed");
    registerCallback(jj, "taskStopped");
    registerCallback(jj, "workSiteChanged");
    return { installed: true };
  }

  function invokeHttp(jj, config, callbackName, raw) {
    try {
      validateConfig(config);
      var response;
      if (callbackName === "tkgValidateTransport") response = validateTransport(jj, config, raw);
      else if (callbackName === "tkgCreateTransport") response = createTransport(jj, config, raw);
      else if (callbackName === "tkgUpdateContent") response = updateContent(jj, config, raw);
      else response = fail(404, "INTEGRATION_HANDLER_NOT_FOUND");
      return { code: 200, body: JSON.stringify(response) };
    } catch (_) {
      return { code: 500, body: JSON.stringify(fail(500, "INTEGRATION_HANDLER_FAILED")) };
    }
  }

  function validateTransport(jj, config, raw) {
    var request = parseRequest(raw);
    if (!secureEqual(request.integrationKey, config.integrationKey)) return fail(401, "UNAUTHORIZED");
    var shape = validateShape(request);
    if (shape) return fail(422, shape);
    var labelExists = Boolean(jj.isTaskLabelExist(request.taskLabel));
    var recordExists = Boolean(jj.isTaskRecordIdExist(request.taskRecordId));
    return ok({
      validated: labelExists && !recordExists,
      taskLabelExists: labelExists,
      taskRecordExists: recordExists
    }, labelExists ? "VALIDATED" : "TASK_LABEL_NOT_FOUND");
  }

  function createTransport(jj, config, raw) {
    var request = parseRequest(raw);
    if (!secureEqual(request.integrationKey, config.integrationKey)) return fail(401, "UNAUTHORIZED");
    var shape = validateShape(request);
    if (shape) return fail(422, shape);
    if (!jj.isTaskLabelExist(request.taskLabel)) return fail(404, "TASK_LABEL_NOT_FOUND");
    if (jj.isTaskRecordIdExist(request.taskRecordId)) {
      return ok({
        idempotentReplay: true,
        taskRecord: parseUnknown(jj.getTaskRecordById(request.taskRecordId))
      }, "IDEMPOTENT_REPLAY");
    }

    var taskParam = {
      taskRecordId: request.taskRecordId,
      taskLabel: request.taskLabel,
      priority: request.priority,
      inputParams: JSON.stringify(request.inputParams)
    };
    var createResult = parseUnknown(jj.createWindTask(JSON.stringify(taskParam)));
    if (createResult && typeof createResult === "object" && createResult.code !== undefined && createResult.code !== 200) {
      return fail(502, "RDS_CREATE_REJECTED");
    }
    return ok({ idempotentReplay: false, taskRecordId: request.taskRecordId }, "CREATE_ACCEPTED");
  }

  function updateContent(jj, config, raw) {
    var request = parseRequest(raw);
    if (!secureEqual(request.integrationKey, config.integrationKey)) return fail(401, "UNAUTHORIZED");
    if (!isNonEmptyString(request.siteId) || typeof request.content !== "string") return fail(422, "INVALID_CONTENT_REQUEST");
    jj.updateSitesByCondition(
      JSON.stringify({ siteIds: [request.siteId] }),
      JSON.stringify({ content: request.content })
    );
    return ok({ siteId: request.siteId, contentChanged: true, filledChanged: false }, "CONTENT_UPDATE_ACCEPTED");
  }

  function forwardEvent(jj, config, eventType, raw) {
    validateConfig(config);
    var body = {
      eventType: eventType,
      rawPayload: parseUnknown(raw),
      eventTimestamp: new Date().toISOString()
    };
    return jj.requestHttpPost(
      config.callbackUrl,
      JSON.stringify(body),
      JSON.stringify({ "X-TKG-RDS-Key": config.callbackKey }),
      "JSON"
    );
  }

  function registerPost(jj, path, callbackName) {
    jj.registerHandler("POST", path, callbackName, false);
  }

  function registerCallback(jj, eventType) {
    jj.registerTaskEventFunction(eventType);
  }

  function validateShape(request) {
    if (!isNonEmptyString(request.taskRecordId)) return "TASK_RECORD_ID_REQUIRED";
    if (!isNonEmptyString(request.taskLabel)) return "TASK_LABEL_REQUIRED";
    if (!request.inputParams || typeof request.inputParams !== "object" || Array.isArray(request.inputParams)) return "INPUT_PARAMS_REQUIRED";
    if (request.priority !== undefined && (!Number.isInteger(request.priority) || request.priority < 0)) return "INVALID_PRIORITY";
    return null;
  }

  function validateConfig(config) {
    if (!config || !isNonEmptyString(config.integrationKey) || !isNonEmptyString(config.callbackKey) || !isNonEmptyString(config.callbackUrl)) {
      throw new Error("TKG_RDS_INTEGRATION_CONFIG is incomplete");
    }
  }

  function parseRequest(raw) {
    var parsed = parseUnknown(raw);
    return parsed && typeof parsed === "object" && !Array.isArray(parsed) ? parsed : {};
  }

  function parseUnknown(raw) {
    if (typeof raw !== "string") return raw;
    try { return JSON.parse(raw); } catch (_) { return { raw: raw }; }
  }

  function secureEqual(left, right) {
    if (typeof left !== "string" || typeof right !== "string") return false;
    var mismatch = left.length ^ right.length;
    var max = Math.max(left.length, right.length);
    for (var index = 0; index < max; index += 1) {
      mismatch |= (left.charCodeAt(index % Math.max(1, left.length)) || 0)
        ^ (right.charCodeAt(index % Math.max(1, right.length)) || 0);
    }
    return mismatch === 0;
  }

  function requireFunction(value, name) {
    if (!value || typeof value[name] !== "function") throw new Error("RDS Script Method is unavailable: " + name);
  }

  function isNonEmptyString(value) { return typeof value === "string" && value.trim().length > 0; }
  function ok(data, message) { return { code: 200, message: message, data: data }; }
  function fail(code, message) { return { code: code, message: message, data: null }; }

  return { install: install, invokeHttp: invokeHttp, forwardEvent: forwardEvent, secureEqual: secureEqual };
});

// RDS resolves registered handlers and task events only from static top-level functions.
function tkgValidateTransport(param) { return TkgRdsIntegration.invokeHttp(jj, TKG_RDS_INTEGRATION_CONFIG, "tkgValidateTransport", param); }
function tkgCreateTransport(param) { return TkgRdsIntegration.invokeHttp(jj, TKG_RDS_INTEGRATION_CONFIG, "tkgCreateTransport", param); }
function tkgUpdateContent(param) { return TkgRdsIntegration.invokeHttp(jj, TKG_RDS_INTEGRATION_CONFIG, "tkgUpdateContent", param); }
function taskDone(param) { return TkgRdsIntegration.forwardEvent(jj, TKG_RDS_INTEGRATION_CONFIG, "taskDone", param); }
function taskFailed(param) { return TkgRdsIntegration.forwardEvent(jj, TKG_RDS_INTEGRATION_CONFIG, "taskFailed", param); }
function taskStopped(param) { return TkgRdsIntegration.forwardEvent(jj, TKG_RDS_INTEGRATION_CONFIG, "taskStopped", param); }
function workSiteChanged(param) { return TkgRdsIntegration.forwardEvent(jj, TKG_RDS_INTEGRATION_CONFIG, "workSiteChanged", param); }


function boot() {
  TkgRdsIntegration.install(jj, TKG_RDS_INTEGRATION_CONFIG);
}
