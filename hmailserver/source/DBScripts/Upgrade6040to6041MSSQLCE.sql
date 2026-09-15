create table hm_calendars
(
	calendarid int identity(1,1) not null,
	calendaraccountid int not null,
	calendarname nvarchar(255) not null,
	calendardisplayname nvarchar(255) not null,
	calendarsynctoken bigint not null,
	calendarcreated bigint not null
)

ALTER TABLE hm_calendars ADD CONSTRAINT hm_calendars_pk PRIMARY KEY (calendarid)

CREATE UNIQUE INDEX idx_hm_calendars_account ON hm_calendars (calendaraccountid, calendarname)

ALTER TABLE hm_calendars ADD CONSTRAINT fk_hm_calendars_account FOREIGN KEY (calendaraccountid) REFERENCES hm_accounts (accountid) ON DELETE CASCADE

create table hm_calendarobjects
(
	objectid int identity(1,1) not null,
	objectaccountid int not null,
	objectcalendarid int not null,
	objecturi nvarchar(255) not null,
	objectuid nvarchar(255) not null,
	objectcomponent nvarchar(16) not null,
	objectdata ntext not null,
	objectetag nvarchar(64) not null,
	objectsynctoken bigint not null,
	objectstart bigint not null,
	objectend bigint not null,
	objectfirst bigint not null,
	objectlast bigint not null,
	objectdeleted tinyint not null,
	objectmodified bigint not null
)

ALTER TABLE hm_calendarobjects ADD CONSTRAINT hm_calendarobjects_pk PRIMARY KEY (objectid)

CREATE UNIQUE INDEX idx_hm_calendarobjects_uri ON hm_calendarobjects (objectcalendarid, objecturi)

CREATE INDEX idx_hm_calendarobjects_sync ON hm_calendarobjects (objectcalendarid, objectsynctoken)

ALTER TABLE hm_calendarobjects ADD CONSTRAINT fk_hm_calendarobjects_calendar FOREIGN KEY (objectcalendarid) REFERENCES hm_calendars (calendarid) ON DELETE CASCADE

update hm_dbversion set value = 6041
