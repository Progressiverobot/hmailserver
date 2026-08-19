create table hm_apppasswords
(
	apid int auto_increment not null, primary key(`apid`), unique(`apid`),
	apaccountid int not null,
	apname varchar(255) not null,
	aphash varchar(255) not null,
	apencryption int not null,
	apcreated datetime not null,
	aplastused datetime null,
	apactive tinyint not null
) DEFAULT CHARSET=utf8;

CREATE INDEX idx_hm_apppasswords ON hm_apppasswords (apaccountid);

update hm_dbversion set value = 6016;
