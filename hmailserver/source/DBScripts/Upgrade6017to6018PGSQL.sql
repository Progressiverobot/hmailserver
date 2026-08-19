create table hm_quarantine
(
	quarantineid bigserial not null primary key,
	quarantinefilename varchar(255) not null,
	quarantinesender varchar(255) not null,
	quarantinerecipients varchar(1000) not null,
	quarantinesubject varchar(255) not null,
	quarantinereason varchar(255) not null,
	quarantinescore int not null,
	quarantinesize int not null,
	quarantinecreated timestamp not null
);

CREATE INDEX idx_hm_quarantine_created ON hm_quarantine (quarantinecreated);

update hm_dbversion set value = 6018;
