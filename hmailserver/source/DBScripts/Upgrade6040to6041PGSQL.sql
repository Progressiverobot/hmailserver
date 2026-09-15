create table hm_calendars
(
	calendarid bigserial not null primary key,
	calendaraccountid int not null,
	calendarname varchar(255) not null,
	calendardisplayname varchar(255) not null,
	calendarsynctoken bigint not null,
	calendarcreated bigint not null
);

CREATE UNIQUE INDEX idx_hm_calendars_account ON hm_calendars (calendaraccountid, calendarname);

ALTER TABLE hm_calendars ADD CONSTRAINT fk_hm_calendars_account FOREIGN KEY (calendaraccountid) REFERENCES hm_accounts (accountid) ON DELETE CASCADE;

create table hm_calendarobjects
(
	objectid bigserial not null primary key,
	objectaccountid int not null,
	objectcalendarid int not null,
	objecturi varchar(255) not null,
	objectuid varchar(255) not null,
	objectcomponent varchar(16) not null,
	objectdata text not null,
	objectetag varchar(64) not null,
	objectsynctoken bigint not null,
	objectstart bigint not null,
	objectend bigint not null,
	objectfirst bigint not null,
	objectlast bigint not null,
	objectdeleted smallint not null,
	objectmodified bigint not null
);

CREATE UNIQUE INDEX idx_hm_calendarobjects_uri ON hm_calendarobjects (objectcalendarid, objecturi);
CREATE INDEX idx_hm_calendarobjects_sync ON hm_calendarobjects (objectcalendarid, objectsynctoken);

ALTER TABLE hm_calendarobjects ADD CONSTRAINT fk_hm_calendarobjects_calendar FOREIGN KEY (objectcalendarid) REFERENCES hm_calendars (calendarid) ON DELETE CASCADE;

update hm_dbversion set value = 6041;
