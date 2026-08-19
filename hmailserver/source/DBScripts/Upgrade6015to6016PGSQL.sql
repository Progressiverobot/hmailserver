create table hm_apppasswords
(
	apid bigserial not null primary key,
	apaccountid int not null,
	apname varchar(255) not null,
	aphash varchar(255) not null,
	apencryption int not null,
	apcreated timestamp not null,
	aplastused timestamp null,
	apactive smallint not null
);

CREATE INDEX idx_hm_apppasswords ON hm_apppasswords (apaccountid);

update hm_dbversion set value = 6016;
