"""Regressions for false PASS conditions; these are assertion unit tests, not E2E."""
import unittest
from test_operator_web_e2e import all_verifications_pass, validate_projection, physical_preflight_block


class BrowserAssertions(unittest.TestCase):
    def test_unavailable_mapped_physical_slot_blocks_before_browser_mutation(self):
        self.assertEqual("RDS_WORKSITE_STALE", physical_preflight_block({"slots": [{"transportEligible": True,
            "executionReadiness": {"rdsWorkSiteMapped": True, "isExecutionReady": False,
            "readinessCode": "RDS_WORKSITE_STALE"}}]}))
        self.assertIsNone(physical_preflight_block({"slots": [{"transportEligible": True,
            "executionReadiness": {"rdsWorkSiteMapped": True, "isExecutionReady": True}}]}))

    def test_missing_physical_preflight_is_not_a_pass(self):
        with self.assertRaises(RuntimeError):
            physical_preflight_block({})

    def projection(self, full=False, empty=False, state="READY"):
        return [{"positionNumber": 1, "fullAction": {"enabled": full},
                 "emptyAction": {"enabled": empty}, "transportState": state}]

    def test_counts_alone_cannot_prove_enabled_state(self):
        self.assertFalse(validate_projection(self.projection(), [{"fullEnabled": True, "emptyEnabled": False, "badge": "Taşıma durumu Hazır"}]))

    def test_empty_or_failed_api_projection_cannot_pass(self):
        self.assertFalse(validate_projection([], []))

    def test_green_badge_with_no_executable_action_fails(self):
        self.assertFalse(validate_projection(self.projection(), [{"fullEnabled": False, "emptyEnabled": False, "badge": "Taşıma durumu Hazır"}]))

    def test_missing_badge_cannot_pass(self):
        self.assertFalse(validate_projection(self.projection(), [{"fullEnabled": False, "emptyEnabled": False}]))

    def test_honest_disabled_configuration_projection_passes(self):
        self.assertTrue(validate_projection(self.projection(), [{"fullEnabled": False, "emptyEnabled": False, "badge": "Taşıma durumu Yapılandırma Eksik"}]))

    def test_not_ready_badge_is_distinct_from_ready(self):
        self.assertTrue(validate_projection(self.projection(), [{"fullEnabled": False, "emptyEnabled": False, "badge": "Taşıma durumu Hazır Değil"}]))

    def test_one_failed_verification_or_no_verifications_fails(self):
        self.assertFalse(all_verifications_pass({"labels": {"pass": True}, "transport": {"pass": False}}))
        self.assertFalse(all_verifications_pass({}))

    def test_active_transport_buttons_must_be_disabled(self):
        self.assertFalse(validate_projection(self.projection(True, False, "QUEUED"), [{"fullEnabled": True, "emptyEnabled": False, "badge": "Taşıma durumu Sırada"}]))


if __name__ == '__main__':
    unittest.main()
