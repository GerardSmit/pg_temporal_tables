EXTENSION = pg_temporal_tables
EXTVERSION = 0.1.1

DATA = sql/pg_temporal_tables--$(EXTVERSION).sql \
	sql/pg_temporal_tables--0.1.0--0.1.1.sql

MODULE_big = pg_temporal_tables
OBJS = \
	src/pg_temporal_tables.o \
	src/temporal_catalog.o \
	src/temporal_trigger.o \
	src/temporal_hooks.o

PG_CPPFLAGS = -I$(srcdir)/src
PG_CFLAGS += -Wall -Wextra -Wno-unused-parameter -Wno-declaration-after-statement \
	-Werror=implicit-function-declaration

PG_CONFIG ?= pg_config
PGXS := $(shell $(PG_CONFIG) --pgxs)
include $(PGXS)
