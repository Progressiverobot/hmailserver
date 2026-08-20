create table hm_messagetrace
(
	mtid bigserial not null primary key,
	mtqueueid int not null,
	mtoccurred timestamp not null,
	mtevent varchar(32) not null,
	mtsender varchar(255) not null,
	mtrecipient varchar(255) not null,
	mtsourceip varchar(64) not null,
	mtstatuscode int not null,
	mtdetail varchar(255) not null
);


CREATE INDEX idx_hm_messagetrace ON hm_messagetrace (mtoccurred);

update hm_dbversion set value = 6020;
