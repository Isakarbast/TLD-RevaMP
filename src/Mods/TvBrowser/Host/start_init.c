/* The program's own constructors, as glibc's __libc_csu_init ran them before 2.34 (see start.S). */
typedef void (*init_fn)(int, char**, char**);
extern init_fn __preinit_array_start[] __attribute__((visibility("hidden")));
extern init_fn __preinit_array_end[] __attribute__((visibility("hidden")));
extern init_fn __init_array_start[] __attribute__((visibility("hidden")));
extern init_fn __init_array_end[] __attribute__((visibility("hidden")));
extern void _init(void);

void tvb_init(int argc, char** argv, char** envp) {
  for (init_fn* f = __preinit_array_start; f < __preinit_array_end; f++) (*f)(argc, argv, envp);
  _init();
  for (init_fn* f = __init_array_start; f < __init_array_end; f++) (*f)(argc, argv, envp);
}

