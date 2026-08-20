create table hm_messagetrace
(
	mtid int identity(1,1) not null,
	mtqueueid int not null,
	mtoccurred datetime not null,
	mtevent nvarchar(32) not null,
	mtsender nvarchar(255) not null,
	mtrecipient nvarchar(255) not null,
	mtsourceip nvarchar(64) not null,
	mtstatuscode int not null,
	mtdetail nvarchar(255) not null
)
ALTER TABLE hm_messagetrace ADD CONSTRAINT hm_messagetrace_pk PRIMARY KEY NONCLUSTERED (mtid)

CREATE CLUSTERED INDEX idx_hm_messagetrace ON hm_messagetrace (mtoccurred)

update hm_dbversion set value = 6020
