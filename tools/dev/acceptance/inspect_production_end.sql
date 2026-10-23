BEGIN TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY;
WITH RECURSIVE target AS (SELECT * FROM ops.production_sessions WHERE id=:'session_id'::uuid),
runs AS (SELECT r.* FROM ops.production_end_runs r JOIN target s ON s.id=r.production_session_id),
request_ids(id) AS (
 SELECT r.id FROM ops.transport_requests r WHERE r.production_session_id=:'session_id'::uuid
 UNION
 SELECT CASE WHEN d.predecessor_request_id=q.id THEN d.successor_request_id ELSE d.predecessor_request_id END
 FROM request_ids q JOIN ops.transport_request_dependencies d ON q.id IN(d.predecessor_request_id,d.successor_request_id)
), requests AS (SELECT r.* FROM ops.transport_requests r WHERE r.id IN(SELECT id FROM request_ids) OR r.id IN(SELECT transport_request_id FROM ops.production_end_run_items WHERE run_id IN(SELECT id FROM runs))),
slots AS (SELECT layout_slot_id FROM ops.production_end_run_items WHERE run_id IN(SELECT id FROM runs)
 UNION SELECT id FROM master.layout_slots WHERE layout_id IN(SELECT layout_id FROM runs UNION SELECT active_layout_id FROM ops.machine_runtime_state WHERE machine_id IN(SELECT machine_id FROM target))
 UNION SELECT source_slot_id FROM requests UNION SELECT destination_slot_id FROM requests)
SELECT json_build_object(
 'observed_at', clock_timestamp(),
 'session', (SELECT row_to_json(q) FROM (SELECT s.*,m.code AS machine_code,v.code AS variant_code,p.part_code,u.username AS actor_username,u.employee_no AS actor_employee_number,d.device_code FROM target s JOIN master.machines m ON m.id=s.machine_id JOIN master.variants v ON v.id=s.variant_id JOIN master.parts p ON p.id=s.main_part_id JOIN auth.users u ON u.id=s.started_by JOIN audit.client_devices d ON d.id=s.started_device_id) q),
 'runs', (SELECT coalesce(json_agg(r ORDER BY r.created_at),'[]'::json) FROM runs r),
 'items', (SELECT coalesce(json_agg(i ORDER BY run_id,sequence_no),'[]'::json) FROM ops.production_end_run_items i WHERE run_id IN(SELECT id FROM runs)),
 'requests', (SELECT coalesce(json_agg(q ORDER BY q.request_no),'[]'::json) FROM (SELECT r.*,t.code AS operation,src.code AS source_slot_code,dst.code AS destination_slot_code FROM requests r JOIN master.task_types t ON t.id=r.task_type_id LEFT JOIN master.layout_slots src ON src.id=r.source_slot_id LEFT JOIN master.layout_slots dst ON dst.id=r.destination_slot_id) q),
 'dependencies', (SELECT coalesce(json_agg(d),'[]'::json) FROM ops.transport_request_dependencies d WHERE predecessor_request_id IN(SELECT id FROM requests) OR successor_request_id IN(SELECT id FROM requests)),
 'events', (SELECT coalesce(json_agg(q ORDER BY q.id),'[]'::json) FROM (SELECT id,transport_request_id,actor_user_id,device_id,event_type,old_status,new_status,message,created_at,correlation_id, data_json - ARRAY['password','token','accessToken','key','secret','authorization'] AS data_json FROM ops.transport_request_events WHERE transport_request_id IN(SELECT id FROM requests)) q),
 'rds_tasks', (SELECT coalesce(json_agg(q ORDER BY q.sent_at),'[]'::json) FROM (SELECT id,transport_request_id,rds_task_record_id,rds_task_label,out_order_no,agv_id,rds_status,ended_reason,sent_at,started_at,ended_at,last_sync_at,created_at,updated_at FROM integration.rds_tasks WHERE transport_request_id IN(SELECT id FROM requests)) q),
 'audit', (SELECT coalesce(json_agg(q ORDER BY q.id),'[]'::json) FROM (SELECT id,actor_user_id,device_id,action,entity_type,entity_id,ip_address,user_agent,created_at,correlation_id,before_json,after_json FROM audit.audit_logs WHERE entity_id=:'session_id' OR entity_id IN(SELECT id::text FROM runs UNION SELECT id::text FROM requests)) q),
 'slots', (SELECT coalesce(json_agg(q ORDER BY q.slot_code),'[]'::json) FROM (SELECT s.*,l.code AS slot_code FROM ops.slot_runtime_state s JOIN master.layout_slots l ON l.id=s.layout_slot_id WHERE layout_slot_id IN(SELECT layout_slot_id FROM slots)) q),
 'runtime', (SELECT row_to_json(r) FROM ops.machine_runtime_state r WHERE machine_id IN(SELECT machine_id FROM target)),
 'preparation', (SELECT coalesce(json_agg(p),'[]'::json) FROM ops.preparation_pallets p WHERE transport_request_id IN(SELECT id FROM requests)),
 'store_units', (SELECT coalesce(json_agg(u),'[]'::json) FROM ops.store_units u WHERE transport_request_id IN(SELECT id FROM requests))
);
COMMIT;
