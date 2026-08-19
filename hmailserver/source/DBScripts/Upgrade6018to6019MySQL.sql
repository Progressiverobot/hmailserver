ALTER TABLE hm_accounts ADD accountpasswordchanged datetime not null DEFAULT '2000-01-01';

update hm_accounts set accountpasswordchanged = now();

create table hm_passwordhistory
(
	phid int auto_increment not null, primary key(`phid`), unique(`phid`),
	phaccountid int not null,
	phhash varchar(255) not null,
	phencryption int not null,
	phchanged datetime not null
) DEFAULT CHARSET=utf8;

CREATE INDEX idx_hm_passwordhistory ON hm_passwordhistory (phaccountid);

update hm_dbversion set value = 6019;
