create table hm_audit
(
	auditid bigserial not null primary key,
	audittime bigint not null,
	auditactor varchar(255) not null,
	auditactortype varchar(32) not null,
	auditinterface varchar(32) not null,
	auditaddress varchar(64) not null,
	auditobjecttype varchar(64) not null,
	auditobjectname varchar(255) not null,
	auditaction varchar(32) not null,
	auditdetail text not null,
	auditprevhash varchar(64) not null,
	audithash varchar(64) not null
);

CREATE INDEX idx_hm_audit_time ON hm_audit (audittime);

CREATE INDEX idx_hm_audit_actor ON hm_audit (auditactor);

create table hm_alertrules
(
	alertruleid bigserial not null primary key,
	alertrulecondition varchar(64) not null,
	alertruleenabled smallint not null,
	alertrulethreshold int not null,
	alertruleactions int not null,
	alertruleaddress varchar(255) not null,
	alertrulewebhook varchar(1024) not null,
	alertrulesecret varchar(255) not null,
	alertrulecooldown int not null,
	alertruledigest smallint not null
);

CREATE UNIQUE INDEX idx_hm_alertrules_condition ON hm_alertrules (alertrulecondition);

create table hm_alertevents
(
	alerteventid bigserial not null primary key,
	alerteventcondition varchar(64) not null,
	alerteventseverity int not null,
	alerteventstate int not null,
	alerteventtime bigint not null,
	alerteventsummary varchar(255) not null,
	alerteventdetail text not null,
	alerteventnotified smallint not null,
	alerteventdigested smallint not null,
	alerteventhookstate int not null,
	alerteventhooktries int not null,
	alerteventhooknext bigint not null
);

CREATE INDEX idx_hm_alertevents_time ON hm_alertevents (alerteventtime);

CREATE INDEX idx_hm_alertevents_condition ON hm_alertevents (alerteventcondition, alerteventtime);

insert into hm_alertrules (alertrulecondition, alertruleenabled, alertrulethreshold, alertruleactions, alertruleaddress, alertrulewebhook, alertrulesecret, alertrulecooldown, alertruledigest) values ('backup.failed', 1, 0, 1, '', '', '', 60, 0);

insert into hm_alertrules (alertrulecondition, alertruleenabled, alertrulethreshold, alertruleactions, alertruleaddress, alertrulewebhook, alertrulesecret, alertrulecooldown, alertruledigest) values ('certificate.expiring', 1, 7, 1, '', '', '', 1440, 1);

insert into hm_alertrules (alertrulecondition, alertruleenabled, alertrulethreshold, alertruleactions, alertruleaddress, alertrulewebhook, alertrulesecret, alertrulecooldown, alertruledigest) values ('disk.low', 0, 0, 1, '', '', '', 60, 1);

insert into hm_alertrules (alertrulecondition, alertruleenabled, alertrulethreshold, alertruleactions, alertruleaddress, alertrulewebhook, alertrulesecret, alertrulecooldown, alertruledigest) values ('queue.stalled', 0, 0, 1, '', '', '', 60, 1);

insert into hm_alertrules (alertrulecondition, alertruleenabled, alertrulethreshold, alertruleactions, alertruleaddress, alertrulewebhook, alertrulesecret, alertrulecooldown, alertruledigest) values ('autoban.storm', 0, 25, 1, '', '', '', 60, 1);

insert into hm_alertrules (alertrulecondition, alertruleenabled, alertrulethreshold, alertruleactions, alertruleaddress, alertrulewebhook, alertrulesecret, alertrulecooldown, alertruledigest) values ('minidump.written', 0, 0, 1, '', '', '', 60, 1);

insert into hm_settings (settingname, settingstring, settinginteger) select 'AlertsEnabled', '', 1 from hm_dbversion where not exists (select settingname from hm_settings where settingname = 'AlertsEnabled');

insert into hm_settings (settingname, settingstring, settinginteger) select 'AlertRecipient', '', 0 from hm_dbversion where not exists (select settingname from hm_settings where settingname = 'AlertRecipient');

insert into hm_settings (settingname, settingstring, settinginteger) select 'AlertSenderAddress', '', 0 from hm_dbversion where not exists (select settingname from hm_settings where settingname = 'AlertSenderAddress');

insert into hm_settings (settingname, settingstring, settinginteger) select 'AlertDigestEnabled', '', 1 from hm_dbversion where not exists (select settingname from hm_settings where settingname = 'AlertDigestEnabled');

insert into hm_settings (settingname, settingstring, settinginteger) select 'AlertDigestHour', '', 7 from hm_dbversion where not exists (select settingname from hm_settings where settingname = 'AlertDigestHour');

insert into hm_settings (settingname, settingstring, settinginteger) select 'AlertMaxPerHour', '', 20 from hm_dbversion where not exists (select settingname from hm_settings where settingname = 'AlertMaxPerHour');

insert into hm_settings (settingname, settingstring, settinginteger) select 'AlertWebhookMaxAttempts', '', 5 from hm_dbversion where not exists (select settingname from hm_settings where settingname = 'AlertWebhookMaxAttempts');

insert into hm_settings (settingname, settingstring, settinginteger) select 'AuditTrailEnabled', '', 1 from hm_dbversion where not exists (select settingname from hm_settings where settingname = 'AuditTrailEnabled');

insert into hm_settings (settingname, settingstring, settinginteger) select 'AuditRetentionDays', '', 0 from hm_dbversion where not exists (select settingname from hm_settings where settingname = 'AuditRetentionDays');

update hm_dbversion set value = 6043;
