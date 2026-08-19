create table hm_quarantine
(
	quarantineid int identity(1,1) not null,
	quarantinefilename nvarchar(255) not null,
	quarantinesender nvarchar(255) not null,
	quarantinerecipients nvarchar(1000) not null,
	quarantinesubject nvarchar(255) not null,
	quarantinereason nvarchar(255) not null,
	quarantinescore int not null,
	quarantinesize int not null,
	quarantinecreated datetime not null
)

ALTER TABLE hm_quarantine ADD CONSTRAINT hm_quarantine_pk PRIMARY KEY NONCLUSTERED (quarantineid)

CREATE CLUSTERED INDEX idx_hm_quarantine_created ON hm_quarantine (quarantinecreated)

update hm_dbversion set value = 6018
