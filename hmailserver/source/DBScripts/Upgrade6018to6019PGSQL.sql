ALTER TABLE hm_accounts ADD accountpasswordchanged timestamp not null DEFAULT '2000-01-01';

update hm_accounts set accountpasswordchanged = current_timestamp;

create table hm_passwordhistory
(
	phid bigserial not null primary key,
	phaccountid int not null,
	phhash varchar(255) not null,
	phencryption int not null,
	phchanged timestamp not null
);

CREATE INDEX idx_hm_passwordhistory ON hm_passwordhistory (phaccountid);

update hm_dbversion set value = 6019;
