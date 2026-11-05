// Stand-ins for the two accessibility libraries Chromium links against that Steam's newest runtime (Proton 11's
// container) lacks: libatk-bridge-2.0.so.0 and libatspi.so.0. The TV's pages have no screen reader, so nothing
// here does anything: every function answers "not available". Built twice, once as each library (build.sh).
#include <stddef.h>
#ifdef TVB_ATK_BRIDGE
int atk_bridge_adaptor_init(int* argc, char*** argv) { (void)argc; (void)argv; return -1; }
void atk_bridge_adaptor_cleanup(void) {}
#else
void* atspi_accessible_get_application() { return NULL; }
void* atspi_accessible_get_attributes() { return NULL; }
void* atspi_accessible_get_child_at_index() { return NULL; }
int atspi_accessible_get_child_count() { return 0; }
void* atspi_accessible_get_description() { return NULL; }
void* atspi_accessible_get_interfaces() { return NULL; }
void* atspi_accessible_get_name() { return NULL; }
void* atspi_accessible_get_parent() { return NULL; }
int atspi_accessible_get_process_id() { return 0; }
void* atspi_accessible_get_relation_set() { return NULL; }
int atspi_accessible_get_role() { return 0; }
void* atspi_accessible_get_role_name() { return NULL; }
void* atspi_accessible_get_state_set() { return NULL; }
int atspi_event_get_type() { return 0; }
int atspi_event_listener_deregister(void* a, const char* b, void** e) { (void)a; (void)b; if (e) *e = NULL; return 0; }
void* atspi_event_listener_new() { return NULL; }
int atspi_event_listener_register(void* a, const char* b, void** e) { (void)a; (void)b; if (e) *e = NULL; return 0; }
void* atspi_get_desktop() { return NULL; }
int atspi_init(void) { return 2; }
int atspi_relation_get_n_targets() { return 0; }
int atspi_relation_get_relation_type() { return 0; }
void* atspi_relation_get_target() { return NULL; }
void* atspi_state_set_get_states() { return NULL; }
#endif
