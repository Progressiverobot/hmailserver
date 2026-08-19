ALTER TABLE hm_accounts ADD accountpasswordchanged datetime not null DEFAULT '2000-01-01'

update hm_accounts set accountpasswordchanged = getdate()

create table hm_passwordhistory
(
	phid int identity(1,1) not null,
	phaccountid int not null,
	phhash nvarchar(255) not null,
	phencryption int not null,
	phchanged datetime not null
)

ALTER TABLE hm_passwordhistory ADD CONSTRAINT hm_passwordhistory_pk PRIMARY KEY (phid)

CREATE INDEX idx_hm_passwordhistory ON hm_passwordhistory (phaccountid)

update hm_dbversion set value = 6019
