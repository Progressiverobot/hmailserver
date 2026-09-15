create table hm_audit
(
	auditid bigint identity(1,1) not null,
	audittime bigint not null,
	auditactor nvarchar(255) not null,
	auditactortype nvarchar(32) not null,
	auditinterface nvarchar(32) not null,
	auditaddress nvarchar(64) not null,
	auditobjecttype nvarchar(64) not null,
	auditobjectname nvarchar(255) not null,
	auditaction nvarchar(32) not null,
	auditdetail ntext not null,
	auditprevhash nvarchar(64) not null,
	audithash nvarchar(64) not null
)

ALTER TABLE hm_audit ADD CONSTRAINT hm_audit_pk PRIMARY KEY CLUSTERED (auditid)

CREATE INDEX idx_hm_audit_time ON hm_audit (audittime)

CREATE INDEX idx_hm_audit_actor ON hm_audit (auditactor)

create table hm_alertrules
(
	alertruleid bigint identity(1,1) not null,
	alertrulecondition nvarchar(64) not null,
	alertruleenabled tinyint not null,
	alertrulethreshold int not null,
	alertruleactions int not null,
	alertruleaddress nvarchar(255) not null,
	alertrulewebhook nvarchar(1024) not null,
	alertrulesecret nvarchar(255) not null,
	alertrulecooldown int not null,
	alertruledigest tinyint not null
)

ALTER TABLE hm_alertrules ADD CONSTRAINT hm_alertrules_pk PRIMARY KEY CLUSTERED (alertruleid)

CREATE UNIQUE INDEX idx_hm_alertrules_condition ON hm_alertrules (alertrulecondition)

create table hm_alertevents
(
	alerteventid bigint identity(1,1) not null,
	alerteventcondition nvarchar(64) not null,
	alerteventseverity int not null,
	alerteventstate int not null,
	alerteventtime bigint not null,
	alerteventsummary nvarchar(255) not null,
	alerteventdetail ntext not null,
	alerteventnotified tinyint not null,
	alerteventdigested tinyint not null,
	alerteventhookstate int not null,
	alerteventhooktries int not null,
	alerteventhooknext bigint not null
)

ALTER TABLE hm_alertevents ADD CONSTRAINT hm_alertevents_pk PRIMARY KEY CLUSTERED (alerteventid)

CREATE INDEX idx_hm_alertevents_time ON hm_alertevents (alerteventtime)

CREATE INDEX idx_hm_alertevents_condition ON hm_alertevents (alerteventcondition, alerteventtime)

insert into hm_alertrules (alertrulecondition, alertruleenabled, alertrulethreshold, alertruleactions, alertruleaddress, alertrulewebhook, alertrulesecret, alertrulecooldown, alertruledigest) values ('backup.failed', 1, 0, 1, '', '', '', 60, 0)

insert into hm_alertrules (alertrulecondition, alertruleenabled, alertrulethreshold, alertruleactions, alertruleaddress, alertrulewebhook, alertrulesecret, alertrulecooldown, alertruledigest) values ('certificate.expiring', 1, 7, 1, '', '', '', 1440, 1)

insert into hm_alertrules (alertrulecondition, alertruleenabled, alertrulethreshold, alertruleactions, alertruleaddress, alertrulewebhook, alertrulesecret, alertrulecooldown, alertruledigest) values ('disk.low', 0, 0, 1, '', '', '', 60, 1)

insert into hm_alertrules (alertrulecondition, alertruleenabled, alertrulethreshold, alertruleactions, alertruleaddress, alertrulewebhook, alertrulesecret, alertrulecooldown, alertruledigest) values ('queue.stalled', 0, 0, 1, '', '', '', 60, 1)

insert into hm_alertrules (alertrulecondition, alertruleenabled, alertrulethreshold, alertruleactions, alertruleaddress, alertrulewebhook, alertrulesecret, alertrulecooldown, alertruledigest) values ('autoban.storm', 0, 25, 1, '', '', '', 60, 1)

insert into hm_alertrules (alertrulecondition, alertruleenabled, alertrulethreshold, alertruleactions, alertruleaddress, alertrulewebhook, alertrulesecret, alertrulecooldown, alertruledigest) values ('minidump.written', 0, 0, 1, '', '', '', 60, 1)

insert into hm_settings (settingname, settingstring, settinginteger) values ('AlertsEnabled', '', 1)

insert into hm_settings (settingname, settingstring, settinginteger) values ('AlertRecipient', '', 0)

insert into hm_settings (settingname, settingstring, settinginteger) values ('AlertSenderAddress', '', 0)

insert into hm_settings (settingname, settingstring, settinginteger) values ('AlertDigestEnabled', '', 1)

insert into hm_settings (settingname, settingstring, settinginteger) values ('AlertDigestHour', '', 7)

insert into hm_settings (settingname, settingstring, settinginteger) values ('AlertMaxPerHour', '', 20)

insert into hm_settings (settingname, settingstring, settinginteger) values ('AlertWebhookMaxAttempts', '', 5)

insert into hm_settings (settingname, settingstring, settinginteger) values ('AuditTrailEnabled', '', 1)

insert into hm_settings (settingname, settingstring, settinginteger) values ('AuditRetentionDays', '', 0)

update hm_dbversion set value = 6043
