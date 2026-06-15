/*
 * temporal_hooks.h
 */
#ifndef TEMPORAL_HOOKS_H
#define TEMPORAL_HOOKS_H

extern char *temporal_user_guc;
extern bool temporal_require_user_guc;
extern char *temporal_as_of_guc;
extern bool temporal_internal_ddl_guc;

extern void temporal_hooks_init(void);

#endif							/* TEMPORAL_HOOKS_H */
