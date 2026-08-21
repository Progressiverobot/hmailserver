ALTER TABLE hm_domains ADD domainvacationmessageon int not null DEFAULT 0

ALTER TABLE hm_domains ADD domainvacationsubject nvarchar(200) not null DEFAULT ''

ALTER TABLE hm_domains ADD domainvacationmessage nvarchar(1000) not null DEFAULT ''

ALTER TABLE hm_domains ADD domainvacationinternalsubject nvarchar(200) not null DEFAULT ''

ALTER TABLE hm_domains ADD domainvacationinternalmessage nvarchar(1000) not null DEFAULT ''

ALTER TABLE hm_domains ADD domainvacationexternaloverride int not null DEFAULT 0

create table hm_blocked_senders
(
	bsid bigint identity(1,1) not null,
	bsaddress nvarchar(255) not null,
	bsscore int not null,
	bsdescription nvarchar(255) not null
)

ALTER TABLE hm_blocked_senders ADD CONSTRAINT hm_bsid_pk PRIMARY KEY (bsid)

CREATE UNIQUE INDEX u_bsaddress ON hm_blocked_senders (bsaddress)

update hm_dbversion set value = 6024
