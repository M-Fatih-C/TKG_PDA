#!/usr/bin/env python3
"""
Acceptance test: Real Operator Web Browser E2E Automation.
Automates real user interactions against Flutter Web on http://127.0.0.1:7357
using headless Chrome via Selenium.

Required Flow:
  device setup -> login -> SH-01-A -> real main part -> compatible variant
  -> start production -> reach Mamul Kasa UI
  -> verify BOTH buttons exist: 'Kasa Doldu' and 'Boş Kasa Getir'
  -> verify deprecated labels absent: 'Alan Boş' and 'Kasam Doldu'
  -> verify action states match real API container-position projection
  -> verify status badge honesty (no green Hazır when actions unavailable)
  -> trigger safe active transport -> verify active state in UI
  -> clean up test session and leave DB clean.
"""

import os
import re
import subprocess
import sys
import time
import json
import urllib.request
import urllib.error
import urllib.parse
from pathlib import Path

def check_environment():
    try:
        from selenium import webdriver
        from selenium.webdriver.chrome.options import Options
        from selenium.webdriver.common.by import By
    except ImportError:
        print("SKIP: Selenium package is not installed.")
        sys.exit(2)

    chrome_candidates = [
        os.environ.get("CHROME_PATH"),
        r"C:\Program Files\Google\Chrome\Application\chrome.exe",
        r"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
        r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"
    ]
    chrome_path = next((p for p in chrome_candidates if p and os.path.exists(p)), None)
    if not chrome_path:
        print("SKIP: Google Chrome / Edge executable not found.")
        sys.exit(2)

    return chrome_path

def get_machine_active_session(api_base, device_code, employee_number, machine_code="SH-01-A"):
    try:
        login_url = f"{api_base}/api/v1/auth/login"
        req_data = json.dumps({"employeeNumber": employee_number}).encode("utf-8")
        req = urllib.request.Request(
            login_url,
            data=req_data,
            headers={"Content-Type": "application/json", "X-Device-Code": device_code}
        )
        with urllib.request.urlopen(req, timeout=4) as resp:
            login_res = json.loads(resp.read().decode("utf-8"))
            token = login_res.get("accessToken")

        if not token:
            return None, None

        headers = {
            "Authorization": f"Bearer {token}",
            "X-Device-Code": device_code,
            "Content-Type": "application/json"
        }

        m_req = urllib.request.Request(f"{api_base}/api/v1/machines", headers=headers)
        with urllib.request.urlopen(m_req, timeout=4) as resp:
            machines = json.loads(resp.read().decode("utf-8"))

        mach = next((m for m in machines if m.get("code") == machine_code), None)
        if not mach:
            return None, None

        m_id = mach.get("id")
        try:
            act_req = urllib.request.Request(f"{api_base}/api/v1/machines/{m_id}/production-sessions/active", headers=headers)
            with urllib.request.urlopen(act_req, timeout=4) as resp:
                raw = resp.read().decode("utf-8").strip()
                if resp.status == 204 or not raw:
                    return m_id, None
                return m_id, json.loads(raw)
        except urllib.error.HTTPError as he:
            if he.code == 204:
                return m_id, None
            raise
        except json.JSONDecodeError as error:
            raise RuntimeError('Active-session response was not valid JSON; refusing browser mutation.') from error
    except Exception as e:
        print(f"Active session query note: {e}")
        raise RuntimeError("Active-session preflight could not be verified.") from e

def authenticated_read(api_base, device_code, employee_number, path):
    login = urllib.request.Request(f"{api_base}/api/v1/auth/login",
        data=json.dumps({"employeeNumber": employee_number}).encode(),
        headers={"Content-Type": "application/json", "X-Device-Code": device_code})
    with urllib.request.urlopen(login, timeout=5) as response:
        token = json.load(response)["accessToken"]
    request = urllib.request.Request(api_base + path,
        headers={"Authorization": f"Bearer {token}", "X-Device-Code": device_code})
    with urllib.request.urlopen(request, timeout=5) as response:
        return json.load(response)


def physical_preflight_block(context):
    if not isinstance(context.get("slots"), list):
        raise RuntimeError("Physical slot preflight could not be verified.")
    codes = {slot.get("executionReadiness", {}).get("readinessCode") or "RDS_WORKSITE_STATE_UNKNOWN"
        for slot in context["slots"] if slot.get("transportEligible")
        and slot.get("executionReadiness", {}).get("rdsWorkSiteMapped")
        and not slot.get("executionReadiness", {}).get("isExecutionReady")}
    return ", ".join(sorted(codes)) if codes else None


def validate_projection(positions, cards):
    if not positions or len(positions) != len(cards):
        return False
    active = {"REQUESTED", "QUEUED", "ROBOT_ASSIGNED", "IN_PROGRESS", "WAITING_PREPARATION"}
    for position, card in zip(sorted(positions, key=lambda p: p["positionNumber"]), cards):
        for action, key in (("fullAction", "fullEnabled"), ("emptyAction", "emptyEnabled")):
            if key not in card or card[key] != position[action]["enabled"]:
                return False
        if position["transportState"] in active and (card["fullEnabled"] or card["emptyEnabled"]):
            return False
        executable = position["fullAction"]["enabled"] or position["emptyAction"]["enabled"]
        if not executable and re.search(r"Taşıma durumu Hazır(?:$|\s*·)", card.get("badge", "")):
            return False
        if not card.get("badge"):
            return False
    return True


def all_verifications_pass(verifications):
    return bool(verifications) and all(v.get("pass") is True for v in verifications.values())


def capture_mutations(driver):
    requests, responses = {}, []
    for entry in driver.get_log("performance"):
        message = json.loads(entry["message"])["message"]
        params = message.get("params", {})
        if message["method"] == "Network.requestWillBeSent":
            request = params["request"]
            if request.get("method") == "POST" and ("/production-sessions" in request["url"] or "/container-actions" in request["url"]):
                requests[params["requestId"]] = request
        if message["method"] == "Network.responseReceived":
            responses.append(params)
    result = []
    for response in responses:
        request = requests.get(response["requestId"])
        if request and response["response"]["status"] in (200, 201):
            body = driver.execute_cdp_cmd("Network.getResponseBody", {"requestId": response["requestId"]})
            result.append((request, json.loads(body["body"])))
    return result


def retire_owned_session(session_id, start_key, request_numbers):
    if not re.fullmatch(r"[0-9a-fA-F-]{36}", session_id or "") or not start_key:
        raise RuntimeError("Exact browser-created session id and observed start key are required.")
    database = os.environ.get("TKG_TEST_DATABASE", "tkg_pda_dev")
    if database not in ("tkg_pda_dev", "tkg_pda_acceptance"):
        raise RuntimeError("Unsupported browser test database.")
    key = start_key.replace("'", "''")
    numbers = ",".join(str(int(n)) for n in request_numbers) or "-1"
    sql = f"""
BEGIN;
DO $guard$ BEGIN
 IF NOT EXISTS (SELECT 1 FROM ops.production_sessions WHERE id='{session_id}' AND start_key='{key}')
 THEN RAISE EXCEPTION 'Browser session ownership mismatch'; END IF;
 IF EXISTS (SELECT 1 FROM ops.transport_requests WHERE production_session_id='{session_id}' AND request_no NOT IN ({numbers}))
 THEN RAISE EXCEPTION 'Unowned requests attached to browser session; preserving all rows'; END IF;
END $guard$;
DELETE FROM ops.transport_requests WHERE production_session_id='{session_id}' AND request_no IN ({numbers});
UPDATE ops.production_sessions SET ended_at=GREATEST(clock_timestamp(),started_at),
 ended_by=started_by, ended_device_id=started_device_id, status='ENDED'
WHERE id='{session_id}' AND start_key='{key}' AND ended_at IS NULL;
COMMIT;
"""
    psql=os.environ.get("PSQL_PATH", r"C:\Program Files\PostgreSQL\18\bin\psql.exe")
    completed=subprocess.run([psql,"-X","-h",os.environ.get("TKG_TEST_PG_HOST","127.0.0.1"),
        "-p",os.environ.get("TKG_TEST_PG_PORT","55450"),"-U",os.environ.get("TKG_TEST_PG_USER","postgres"),
        "-d",database,"-v","ON_ERROR_STOP=1"],input=sql,capture_output=True,text=True)
    if completed.returncode:
        raise RuntimeError("Browser-owned cleanup failed; preserve database rows for investigation.")

def run_browser_e2e(report):
    chrome_path = check_environment()
    from selenium import webdriver
    from selenium.webdriver.chrome.options import Options
    from selenium.webdriver.common.by import By

    web_url = os.environ.get("TKG_OPERATOR_WEB_URL", "http://127.0.0.1:7357")
    api_base = os.environ.get("TKG_API_BASE_URL", "http://127.0.0.1:5000")
    device_code = os.environ.get("TKG_TEST_DEVICE_CODE", "DEV-LINE")
    employee_number = os.environ.get("TKG_TEST_EMPLOYEE_NUMBER", "DEV-LINE")

    print("==================================================")
    print("PHASE: OPERATOR WEB REAL BROWSER E2E")
    print("==================================================")
    print(f"Target URL: {web_url}")

    # Check for pre-existing active session on SH-01-A
    sh01a_id, preexisting_session = get_machine_active_session(api_base, device_code, employee_number, "SH-01-A")
    if preexisting_session and preexisting_session.get("id"):
        s_id = preexisting_session.get("id")
        print(f"\nENVIRONMENT_BUSY: Pre-existing active session {s_id} detected on SH-01-A.")
        print("Per acceptance safety rules, browser E2E will NOT mutate or end pre-existing sessions.")
        report["reason"] = "ENVIRONMENT_BUSY — pre-existing production on SH-01-A"
        print("RESULT: SKIP (ENVIRONMENT_BUSY - machine currently in production)")
        sys.exit(2)

    if not sh01a_id:
        raise RuntimeError("Target machine preflight failed; browser must not mutate.")

    context = authenticated_read(api_base, device_code, employee_number,
        f"/api/v1/machines/{sh01a_id}/operational-context")
    physical_block = physical_preflight_block(context)
    if physical_block:
        report["reason"] = f"PHYSICAL_STATE_UNAVAILABLE — {physical_block}"
        print(f"RESULT: SKIP ({report['reason']}); no session or request mutation performed.")
        sys.exit(2)

    # Ensure Flutter Web server bundle is completely compiled before launching Chrome
    bundle_ready = False
    for _ in range(30):
        try:
            with urllib.request.urlopen(f"{web_url}/main.dart.js", timeout=3) as resp:
                if resp.status == 200:
                    bundle_ready = True
                    break
        except Exception:
            pass
        time.sleep(2)

    if not bundle_ready:
        print("WARN: Flutter Web main.dart.js did not return 200 within timeout; proceeding with browser launch anyway...")

    opts = Options()
    opts.add_argument("--headless=new")
    opts.add_argument("--no-sandbox")
    opts.add_argument("--disable-gpu")
    opts.add_argument("--window-size=1366,768")
    opts.binary_location = chrome_path
    opts.set_capability("goog:loggingPrefs", {"browser": "ALL", "performance": "ALL"})

    driver = webdriver.Chrome(options=opts)
    created_session_id = None
    created_start_key = None
    owned_request_numbers = []

    def wait_and_enable_semantics():
        for attempt in range(45):
            try:
                if attempt > 0 and attempt % 10 == 0:
                    driver.get(web_url)
                    time.sleep(3)
                res = driver.execute_script("""
                    try {
                        if (typeof window.$dartRunMain === 'function') {
                            window.$dartRunMain();
                        }
                    } catch (e) {
                        // wait for entrypoint compilation
                    }
                    let sem = document.querySelector('flt-semantics-placeholder');
                    if (sem) {
                        sem.click();
                        return true;
                    }
                    let fltSem = document.querySelector('flt-semantics');
                    if (fltSem) {
                        return true;
                    }
                    return false;
                """)
                if res:
                    time.sleep(2)
                    return True
            except Exception:
                pass
            time.sleep(1)
        return False

    def get_nodes():
        try:
            return driver.execute_script("""
                let nodes = document.querySelectorAll('flt-semantics, input, button');
                return Array.from(nodes).map(el => ({
                    tag: el.tagName,
                    role: el.getAttribute('role'),
                    ariaLabel: el.getAttribute('aria-label'),
                    text: el.innerText ? el.innerText.trim() : '',
                    value: el.value || '',
                    id: el.id
                }));
            """) or []
        except Exception:
            return []

    try:
        # Step 1: Navigate & Enable Semantics
        print("1. Navigating to Operator Web...")
        driver.get(web_url)
        time.sleep(6)
        if not wait_and_enable_semantics():
            raise Exception("Could not initialize Flutter Web application.")
        report["steps"].append("1. Navigated to Web client and enabled semantics")

        # Step 1b: Device Setup (if requested)
        dev_inputs = driver.find_elements(By.XPATH, "//input[contains(@aria-label, 'Cihaz')]")
        if dev_inputs:
            print("1b. Initializing device code...")
            dev_inputs[0].clear()
            dev_inputs[0].send_keys(device_code)
            time.sleep(1)
            save_btn = driver.find_elements(By.XPATH, "//*[@role='button' and contains(., 'Cihaz Kodunu Kaydet')]")
            if save_btn:
                save_btn[0].click()
            time.sleep(3)
            report["steps"].append("1b. Configured device code")

        # Step 2: Login with Employee Number
        emp_inputs = driver.find_elements(By.XPATH, "//input[contains(@aria-label, 'Sicil') or contains(@aria-label, 'sicil')]")
        if emp_inputs:
            print("2. Submitting operator login...")
            emp_inputs[0].clear()
            emp_inputs[0].send_keys(employee_number)
            time.sleep(1)
            login_btn = driver.find_elements(By.XPATH, "//*[@role='button' and contains(., 'Giriş')]")
            if login_btn:
                login_btn[0].click()
            time.sleep(4)
            report["steps"].append("2. Logged in with operator credentials")

        # Step 3: Verify Machine Binding / Selection
        nodes = get_nodes()
        all_text = " ".join((n.get("text") or "") + " " + (n.get("ariaLabel") or "") for n in nodes)
        if "SH-01-A" not in all_text:
            sh01 = driver.find_elements(By.XPATH, "//*[contains(., 'SH-01-A')]")
            if sh01:
                sh01[0].click()
                time.sleep(4)
        report["steps"].append("3. Machine SH-01-A bound")

        # Step 4 & 5: Check production state, select main part if idle
        nodes = get_nodes()
        all_text = " ".join((n.get("text") or "") + " " + (n.get("ariaLabel") or "") for n in nodes)
        if "Üretime Başla" in all_text:
            print("4. Selecting main part 67115BN050-10-HAOS...")
            driver.execute_script("""
                let el = Array.from(document.querySelectorAll('flt-semantics')).find(e => 
                    (e.getAttribute('aria-label') && e.getAttribute('aria-label').includes('67115BN050')) ||
                    (e.innerText && e.innerText.includes('67115BN050'))
                );
                if (el) el.click();
            """)
            time.sleep(4)
            report["steps"].append("4. Selected main part 67115BN050-10-HAOS")
            report["steps"].append("5. Compatible variant resolved")

            # Step 6: Start production
            print("6. Starting production...")
            start_clicked = driver.execute_script("""
                let btns = Array.from(document.querySelectorAll('[role=button]'));
                let b = btns.find(e => e.innerText && e.innerText.includes('Üretime Başla'));
                if (b) {
                    b.click();
                    return true;
                }
                return false;
            """)
            if not start_clicked:
                raise RuntimeError("Production start was not clicked.")
            report["steps"].append("6. Clicked 'Üretime Başla'")

        # Step 7: Reach rendered Mamul Kasa UI (wait up to 15s)
        mamul_kasa_rendered = False
        for attempt in range(15):
            time.sleep(1)
            nodes = get_nodes()
            all_text = " ".join((n.get("text") or "") + " " + (n.get("ariaLabel") or "") for n in nodes)
            if "Mamul Kasa" in all_text:
                mamul_kasa_rendered = True
                print(f"Mamul Kasa rendered after {attempt+1}s!")
                break

        if not mamul_kasa_rendered:
            print("DIAGNOSTIC: Page text snippet:")
            print(all_text[:800])
            raise Exception("Mamul Kasa panel not rendered on machine workspace.")
        report["steps"].append("7. Reached rendered Mamul Kasa UI")

        # Ownership comes from this Chrome instance's actual POST response.
        for request, body in capture_mutations(driver):
            if request["url"].endswith("/production-sessions"):
                created_session_id = body["session"]["id"]
                created_start_key = next((v for k,v in request["headers"].items() if k.lower()=="idempotency-key"), None)
        if not created_session_id or not created_start_key:
            raise RuntimeError("No browser-owned production-start response was observed.")
        _, active_after_start = get_machine_active_session(api_base, device_code, employee_number)
        if not active_after_start or active_after_start["id"] != created_session_id:
            raise RuntimeError("Active session does not match the browser-created session.")

        # Step 8: Verify BOTH buttons exist: 'Kasa Doldu' and 'Boş Kasa Getir'
        has_kasa_doldu = "Kasa Doldu" in all_text
        has_bos_kasa_getir = "Boş Kasa Getir" in all_text
        report["verifications"]["both_buttons_exist"] = {
            "kasa_doldu": has_kasa_doldu,
            "bos_kasa_getir": has_bos_kasa_getir,
            "pass": has_kasa_doldu and has_bos_kasa_getir
        }
        print(f"8. Buttons exist: Kasa Doldu={has_kasa_doldu}, Boş Kasa Getir={has_bos_kasa_getir}")

        # Step 9: Verify deprecated labels do NOT exist: 'Alan Boş', 'Kasam Doldu'
        has_alan_bos = "Alan Boş" in all_text or "Alan Bos" in all_text
        has_kasam_doldu = "Kasam Doldu" in all_text
        report["verifications"]["deprecated_labels_absent"] = {
            "has_alan_bos": has_alan_bos,
            "has_kasam_doldu": has_kasam_doldu,
            "pass": (not has_alan_bos) and (not has_kasam_doldu)
        }
        print(f"9. Deprecated labels absent: Alan Boş={not has_alan_bos}, Kasam Doldu={not has_kasam_doldu}")

        # Fetch the actual authenticated projection, including the active context.
        context = authenticated_read(api_base,device_code,employee_number,f"/api/v1/machines/{sh01a_id}/operational-context")
        params = urllib.parse.urlencode({"variantId":active_after_start["variantId"],
            "layoutId":context["activeLayoutId"],"productGroupId":active_after_start["productGroupId"],
            "partId":active_after_start["mainPartId"]})
        api_positions = authenticated_read(api_base,device_code,employee_number,f"/api/v1/machines/{sh01a_id}/container-positions?{params}")
        cards = driver.execute_script("""
            const text=e=>e.getAttribute('aria-label') || e.innerText || '';
            const action=(e,label)=>Array.from(e.querySelectorAll('[role=button]')).filter(b=>text(b).trim()===label);
            const enabled=e=>e.getAttribute('aria-disabled')!=='true' && !e.disabled;
            const result=[];
            for(const full of Array.from(document.querySelectorAll('[role=button]')).filter(e=>text(e).trim()==='Kasa Doldu')) {
                let card=full.parentElement;
                while(card && !(action(card,'Kasa Doldu').length===1 && action(card,'Boş Kasa Getir').length===1 && Array.from(card.querySelectorAll('[aria-label]')).some(e=>text(e).startsWith('Taşıma durumu ')))) card=card.parentElement;
                if(card) result.push({fullEnabled:enabled(full),emptyEnabled:enabled(action(card,'Boş Kasa Getir')[0]),badge:Array.from(card.querySelectorAll('[aria-label]')).map(text).filter(t=>t.startsWith('Taşıma durumu ')).join(' ')});
            }
            return result;
        """)
        matched = validate_projection(api_positions, cards)
        report["verifications"]["projection_and_badges"]={"pass":matched,"positions":len(api_positions),"cards":len(cards)}
        if not matched:
            raise RuntimeError("Rendered per-position disabled state or readiness badge disagrees with the API.")

        # Step 12: Trigger safe active transport & verify UI update
        print("12. Triggering 'Boş Kasa Getir' on Position 2...")
        driver.execute_script("""
            let btns = Array.from(document.querySelectorAll('[role=button], flt-semantics'));
            let b = btns.find(el => el.getAttribute('aria-label') === 'Boş Kasa Getir' || (el.innerText === 'Boş Kasa Getir' && !el.getAttribute('aria-label')));
            if (b) b.click();
        """)
        time.sleep(5)

        after_nodes = get_nodes()
        after_text = " ".join((n.get("text") or "") + " " + (n.get("ariaLabel") or "") for n in after_nodes)
        for request, body in capture_mutations(driver):
            if request["url"].endswith("/container-actions"):
                owned_request_numbers.append(body["requestNo"])
        fresh=authenticated_read(api_base,device_code,employee_number,f"/api/v1/machines/{sh01a_id}/operational-context")
        active_numbers={r["requestNo"] for r in fresh["activeTransportRequests"]}
        has_active_transport=bool(owned_request_numbers) and all(n in active_numbers and str(n) in after_text for n in owned_request_numbers)
        report["verifications"]["active_transport_rendered"] = {
            "active_transport_present": has_active_transport,
            "pass": has_active_transport
        }
        print(f"12. Active transport rendered in UI: {has_active_transport}")

        if not all_verifications_pass(report["verifications"]):
            raise RuntimeError("At least one required browser verification failed.")
        report["status"] = "PASS"
        print("\nRESULT: PASS (All browser E2E criteria met)")

    except Exception as e:
        report["status"] = "FAIL"
        report["error"] = str(e)
        print(f"\nRESULT: FAIL ({e})")
    finally:
        if created_session_id:
            try:
                retire_owned_session(created_session_id, created_start_key, owned_request_numbers)
            except Exception as cleanup_error:
                report["status"]="FAIL"
                print(str(cleanup_error))
        else:
            print("No browser-created session to clean up.")
        try:
            screenshot = Path(__file__).resolve().parents[3] / "scratch" / "operator_web_e2e_evidence.png"
            driver.save_screenshot(str(screenshot))
            report["screenshot"] = str(screenshot)
        except Exception:
            pass
        try:
            driver.quit()
        except Exception:
            pass

    if report["status"] == "PASS":
        sys.exit(0)
    else:
        sys.exit(1)

if __name__ == "__main__":
    report_path = Path(__file__).resolve().parents[3] / "scratch" / "operator_web_e2e_report.json"
    report_path.parent.mkdir(parents=True, exist_ok=True)
    report = {"status": "RUNNING", "steps": [], "verifications": {},
              "runUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}
    # Replace stale PASS evidence before even the tooling/preflight checks.
    report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    exit_code = 1
    try:
        run_browser_e2e(report)
    except SystemExit as outcome:
        exit_code = outcome.code
        if exit_code == 2:
            report["status"] = "SKIP"
            report.setdefault("reason", "Tooling unavailable")
        elif exit_code != 0:
            report["status"] = "FAIL"
    except Exception as error:
        report["status"] = "FAIL"
        report["error"] = str(error)
        print(f"RESULT: FAIL ({error})")
    finally:
        report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2), encoding="utf-8")
    sys.exit(exit_code)
