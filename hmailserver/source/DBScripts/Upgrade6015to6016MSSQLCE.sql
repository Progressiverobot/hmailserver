create table hm_apppasswords
(
	apid int identity(1,1) not null,
	apaccountid int not null,
	apname nvarchar(255) not null,
	aphash nvarchar(255) not null,
	apencryption int not null,
	apcreated datetime not null,
	aplastused datetime null,
	apactive tinyint not null
)

ALTER TABLE hm_apppasswords ADD CONSTRAINT hm_apppasswords_pk PRIMARY KEY (apid)

CREATE INDEX idx_hm_apppasswords ON hm_apppasswords (apaccountid)

update hm_dbversion set value = 6016
